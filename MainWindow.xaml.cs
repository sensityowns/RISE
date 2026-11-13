using Microsoft.Win32;
using Rise.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Rise;

public partial class MainWindow : Window
{
    private static readonly string[] NavigationItems =
        ["Home", "Optimisation", "Tweaks", "Games", "Game Booster", "Power Plans", "Network", "Debloat", "Tools", "Settings"];
    private static readonly (string Name, string Guid)[] WindowsPlans =
    [
        ("Power Saver", "a1841308-3541-4fab-bc81-f71556f20b4a"),
        ("Balanced", "381b4222-f694-41f0-9685-ff5bb260df2e"),
        ("High Performance", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        ("Ultimate Performance", "e9a42b02-d5df-448d-aa00-03f14749eb61")
    ];

    private readonly TweakService _tweakService = new();
    private readonly GameCatalog _gameCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly StartupManagerService _startupManagerService = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AppSettings _settings;
    private SystemSnapshot? _snapshot;
    private string _currentPage = "Home";
    private string? _originalPowerPlan;
    private TextBlock? _gameMonitorText;
    private TextBlock? _networkResultsText;
    private Process? _monitoredGame;
    private string? _monitoredGameName;
    private string _monitorState = "No game launched by RISE.";
    private readonly List<TextBlock> _homeValues = [];
    private readonly List<TextBlock> _homeDetails = [];
    private readonly List<ProgressBar?> _homeBars = [];
    private TimeSpan? _lastProcessCpuTime;
    private DateTime _lastProcessSampleAt;
    private bool _initialAnimationFinished;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        var appVersion = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.5.0";
        Title = $"RISE · FREE · v{appVersion}";
        ApplyAccent(_settings.Accent);
        _gameCatalog.Detect();
        foreach (var game in GameCatalog.Games)
        {
            if (_settings.GamePaths.TryGetValue(game.Name, out var savedPath) && File.Exists(savedPath))
                GameCatalog.SetExecutable(game, savedPath);
        }

        foreach (var item in TweakService.IsDeveloperBuild
                     ? NavigationItems.Append("Developer")
                     : NavigationItems)
        {
            var button = new Button { Content = item, Style = (Style)FindResource("NavButton"), Tag = item };
            button.Click += (_, _) => ShowPage(item);
            Navigation.Children.Add(button);
        }

        _refreshTimer.Tick += (_, _) => RefreshLiveStatus();
        _refreshTimer.Start();
        Loaded += Window_Loaded;
        ShowPage("Home");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Delay(450);
        Splash.Visibility = Visibility.Collapsed;
        if (_settings.AnimationsEnabled)
        {
            var transform = new TranslateTransform(0, 34);
            AppRoot.RenderTransform = transform;
            var slide = new DoubleAnimation(34, 0, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        }
        else
        {
            Opacity = 1;
        }
        _initialAnimationFinished = true;
    }

    private void ShowPage(string page)
    {
        _currentPage = page;
        PageTitle.Text = page;
        _gameMonitorText = null;
        _networkResultsText = null;
        PageHost.Content = page switch
        {
            "Home" => BuildHome(),
            "Optimisation" => BuildOptimisation(),
            "Tweaks" => BuildTweaks(),
            "Games" => BuildGames(),
            "Game Booster" => BuildGameBooster(),
            "Power Plans" => BuildPowerPlans(),
            "Network" => BuildNetwork(),
            "Debloat" => BuildDebloat(),
            "Tools" => BuildTools(),
            "Settings" => BuildSettings(),
            "Startup Manager" => BuildStartupManager(),
            "Developer" => BuildDeveloperTools(),
            _ => BuildHome()
        };
        if (_initialAnimationFinished && _settings.AnimationsEnabled)
        {
            PageHost.Opacity = 0;
            var transform = new TranslateTransform(0, 9);
            PageHost.RenderTransform = transform;
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            PageHost.Opacity = 1;
            PageHost.RenderTransform = Transform.Identity;
        }
        foreach (var child in Navigation.Children.OfType<Button>())
        {
            var active = Equals(child.Tag, page);
            child.Background = active ? new SolidColorBrush(Color.FromRgb(43, 35, 31)) : Brushes.Transparent;
            child.Foreground = active ? (Brush)FindResource("AccentBrush") : Brushes.WhiteSmoke;
            child.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(79, 53, 39)) : Brushes.Transparent;
        }
    }

    private UIElement BuildHome()
    {
        _snapshot = SystemSnapshotService.Read();
        _homeValues.Clear();
        _homeDetails.Clear();
        _homeBars.Clear();
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Your system, at a glance.", $"RISE FREE · {_tweakService.SupportedFreeCount} supported tweaks · v{GetType().Assembly.GetName().Version?.ToString(3) ?? "0.5.0"} · Live readings from Windows."));

        var overview = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 16) };
        overview.Children.Add(CreateMetricCard("PROCESSOR", _snapshot.Cpu, $"{_snapshot.CpuPercent:0}% system use", _snapshot.CpuPercent / 100));
        overview.Children.Add(CreateMetricCard("MEMORY", $"{_snapshot.RamUsed} / {_snapshot.RamTotal}", $"{_snapshot.RamUsedPercent:0}% in use", _snapshot.RamUsedPercent / 100));
        overview.Children.Add(CreateMetricCard("SYSTEM DRIVE", _snapshot.DiskFree, $"free of {_snapshot.DiskTotal}", 0));
        overview.Children.Add(CreateMetricCard("NETWORK", _snapshot.Network, _snapshot.Uptime + " uptime", 0));
        page.Children.Add(overview);

        var details = new UniformGrid { Columns = 2 };
        var device = new StackPanel();
        AddDetail(device, "Graphics", _snapshot.Gpu);
        AddDetail(device, "Windows", _snapshot.Windows);
        AddDetail(device, "Build", _snapshot.Build);
        AddDetail(device, "Architecture", _snapshot.Architecture);
        AddDetail(device, "Device", _snapshot.FormFactor);
        AddDetail(device, "Battery", _snapshot.Battery);
        AddDetail(device, "Power plan", _snapshot.PowerPlan);
        AddDetail(device, "Activation", _snapshot.Activation);
        details.Children.Add(Card("DEVICE & WINDOWS", device, new Thickness(0, 0, 8, 0)));

        var quick = new StackPanel();
        quick.Children.Add(new TextBlock { Text = "Quick actions", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        quick.Children.Add(Body("RISE only changes the settings you choose. Tweaks shown here are user-level and reversible.", 12));
        quick.Children.Add(ActionButton("Review recommended settings", () => ShowPage("Optimisation"), accent: true, margin: new Thickness(0, 16, 0, 8)));
        quick.Children.Add(ActionButton("Browse 24 supported games", () => ShowPage("Games"), margin: new Thickness(0, 0, 0, 8)));
        quick.Children.Add(ActionButton("Discord community & support", () => OpenUrl("https://discord.com/invite/qfqhJRv8m")));
        details.Children.Add(Card("GET STARTED", quick, new Thickness(8, 0, 0, 0)));
        page.Children.Add(details);
        var membership = new StackPanel();
        membership.Children.Add(new TextBlock { Text = "RISE FREE", FontSize = 15, FontWeight = FontWeights.SemiBold });
        membership.Children.Add(Body($"{_tweakService.SupportedFreeCount} low-risk Free tweaks are currently actionable from the {_tweakService.Definitions.Count}-entry catalog. Higher-risk, administrator-level, shell, service, network, and power actions remain unavailable. Premium actions are not account-verified; this app has no authenticated Pro or owner entitlement system.", 12));
        membership.Children.Add(ActionButton("Visit official RISE website", () => OpenUrl("https://risetweaks.netlify.app/"), accent: true, margin: new Thickness(0, 10, 0, 0)));
        page.Children.Add(Card("EDITION & FEATURES", membership, new Thickness(0, 10, 0, 0)));
        return ScrollPage(page);
    }

    private UIElement BuildOptimisation()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Optimisation", "Choose changes deliberately. Supported changes are backed up and can be restored."));
        page.Children.Add(ActionButton("Apply all supported Free tweaks", () =>
        {
            var supported = _tweakService.Definitions
                .Where(t => t.Tier == "Free" && _tweakService.CanApply(t))
                .ToArray();
            ApplyTweaks(supported);
        }, accent: true, margin: new Thickness(0, 0, 0, 12), enabled: _tweakService.SupportedFreeCount > 0));
        var availability = TweakService.IsDeveloperBuild
            ? "This Debug page lists supported Free tweaks; its bulk action applies Free entries only. The Tweaks library also exposes eligible Premium actions for local development."
            : $"In this Release build, only {_tweakService.SupportedFreeCount} low-risk Free current-user registry tweaks can be applied.";
        page.Children.Add(Body($"The catalog includes {_tweakService.Definitions.Count} entries. {availability} Bulk apply skips all other entries.", 12));
        foreach (var category in _tweakService.Definitions
                     .Where(t => t.Tier == "Free" && _tweakService.CanApply(t))
                     .GroupBy(t => t.Category))
        {
            var group = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
            group.Children.Add(SectionTitle(category.Key));
            foreach (var tweak in category)
                group.Children.Add(BuildTweakCard(tweak));
            page.Children.Add(group);
        }
        page.Children.Add(ActionButton("Restore every RISE-managed tweak", RestoreAllTweaks, margin: new Thickness(0, 4, 0, 10)));
        return ScrollPage(page);
    }

    private UIElement BuildTweaks()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Tweak library", $"{_tweakService.Definitions.Count} catalog entries · {_tweakService.FreeCatalogCount} Free · {_tweakService.PremiumCatalogCount} Premium · {_tweakService.SupportedCount} safely actionable in this build."));
        page.Children.Add(Body("Every supplied entry is listed below. Only validated low-risk HKCU registry toggles are enabled. Raw PowerShell, service, administrator-level registry, adapter, network, and power commands are not executed from this catalog. Premium catalog labels do not imply verified subscriptions.", 12));
        page.Children.Add(ActionButton(
            TweakService.IsDeveloperBuild ? "Apply all safe actions in developer build" : "Apply all supported Free tweaks",
            ApplyAllCatalogTweaks,
            accent: true,
            margin: new Thickness(0, 10, 0, 10),
            enabled: _tweakService.SupportedCount > 0));
        var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var search = new TextBox
        {
            MinWidth = 250,
            Padding = new Thickness(10, 7, 10, 7),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = Brushes.WhiteSmoke,
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ToolTip = "Search tweak names, categories, and descriptions"
        };
        var categoryFilter = new ComboBox
        {
            Width = 180,
            Margin = new Thickness(8, 0, 0, 0),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = Brushes.WhiteSmoke
        };
        categoryFilter.Items.Add("All categories");
        var tierFilter = new ComboBox
        {
            Width = 140,
            Margin = new Thickness(8, 0, 0, 0),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = Brushes.WhiteSmoke
        };
        tierFilter.Items.Add("All tiers");
        tierFilter.Items.Add("Free");
        tierFilter.Items.Add("Premium");
        tierFilter.SelectedIndex = 0;
        foreach (var category in _tweakService.Definitions.Select(t => t.Category).Distinct().OrderBy(c => c))
            categoryFilter.Items.Add(category);
        categoryFilter.SelectedIndex = 0;
        filters.Children.Add(search);
        DockPanel.SetDock(categoryFilter, Dock.Right);
        filters.Children.Add(categoryFilter);
        DockPanel.SetDock(tierFilter, Dock.Right);
        filters.Children.Add(tierFilter);
        page.Children.Add(filters);

        var results = new StackPanel();
        void RefreshResults()
        {
            results.Children.Clear();
            var query = search.Text.Trim();
            var selectedCategory = categoryFilter.SelectedItem?.ToString();
            var selectedTier = tierFilter.SelectedItem?.ToString();
            var matches = _tweakService.Definitions.Where(t =>
                (selectedCategory == "All categories" || t.Category == selectedCategory)
                && (selectedTier == "All tiers" || t.Tier == selectedTier)
                && (string.IsNullOrWhiteSpace(query)
                    || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Risk.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Requires.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (matches.Length == 0)
            {
                results.Children.Add(Body("No catalog entries match this search.", 12));
                return;
            }
            foreach (var category in matches.GroupBy(t => t.Category))
            {
                results.Children.Add(SectionTitle(category.Key));
                foreach (var tweak in category)
                    results.Children.Add(BuildTweakCard(tweak));
            }
        }
        search.TextChanged += (_, _) => RefreshResults();
        categoryFilter.SelectionChanged += (_, _) => RefreshResults();
        tierFilter.SelectionChanged += (_, _) => RefreshResults();
        RefreshResults();
        page.Children.Add(results);
        return ScrollPage(page);
    }

    private UIElement BuildTweakCard(TweakDefinition tweak)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = tweak.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Body(tweak.Description, 12));
        panel.Children.Add(Body($"{tweak.Tier} · {tweak.Risk} risk · {tweak.Kind} · Requires: {tweak.Requires} · Restart: {(tweak.RestartRequired ? "yes" : "no")}", 11));
        panel.Children.Add(Body("Performance effect is not independently benchmarked. Do not expect a guaranteed FPS or latency improvement.", 11));
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var status = new TextBlock { Text = _tweakService.GetStatus(tweak), Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(status, Dock.Right);
        row.Children.Add(status);
        var hasBackup = _tweakService.HasBackup(tweak);
        var supported = _tweakService.CanApply(tweak);
        var isApplied = supported && _tweakService.IsApplied(tweak);
        var actionLabel = !supported ? "Unavailable"
            : isApplied ? hasBackup ? "Undo" : "Already set"
            : "Apply";
        var action = ActionButton(actionLabel,
            () => ChangeTweak(tweak, !isApplied),
            accent: supported && !isApplied,
            enabled: supported && (!isApplied || hasBackup));
        DockPanel.SetDock(action, Dock.Left);
        row.Children.Add(action);
        if (supported && hasBackup && !isApplied)
            row.Children.Add(ActionButton("Undo", () => ChangeTweak(tweak, apply: false), margin: new Thickness(8, 0, 0, 0)));
        panel.Children.Add(row);
        return Card(tweak.Category.ToUpperInvariant(), panel, new Thickness(0, 4, 0, 8));
    }

    private UIElement BuildDeveloperTools()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Developer workspace", "Available only in Debug builds to help the project maintainer edit the local catalog."));
        page.Children.Add(Body("This is a development convenience, not authenticated owner access or a security boundary. Anyone who builds the public source in Debug can see it. Real owner-only permissions require a verified backend, which RISE does not currently have.", 12));
        page.Children.Add(Body($"The editable catalog is loaded from: {_tweakService.CatalogPath}", 12));
        page.Children.Add(ActionButton("Open editable tweak catalog", OpenTweakCatalog, accent: true, margin: new Thickness(0, 12, 0, 8)));
        page.Children.Add(ActionButton("Open all catalog entries", () => ShowPage("Tweaks"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(Body($"Catalog: {_tweakService.Definitions.Count} total · {_tweakService.FreeCatalogCount} Free · {_tweakService.PremiumCatalogCount} Premium · {_tweakService.SupportedCount} safe actions available in this Debug build.", 12));
        page.Children.Add(Body("After editing the JSON, restart RISE to reload it. Only Low-risk Toggle entries with `requires: None` and current-user HKCU registry actions under the allowed Windows settings roots can run. Arbitrary PowerShell, service, network, adapter, powercfg, and administrator-level actions remain blocked.", 12));
        return ScrollPage(page);
    }

    private void OpenTweakCatalog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_tweakService.CatalogPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            MessageBox.Show($"Windows could not open the tweak catalog.\n\n{_tweakService.CatalogPath}\n\n{ex.Message}", "Catalog unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private UIElement BuildGames()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Game library", $"Exactly {GameCatalog.Games.Count} supported games. Detection checks common Steam, Epic, Riot, Rockstar, Xbox, and standalone folders; installations can also be located manually."));
        page.Children.Add(ActionButton("Rescan common game folders", () =>
        {
            _gameCatalog.Detect();
            foreach (var game in GameCatalog.Games)
                if (_settings.GamePaths.TryGetValue(game.Name, out var path) && File.Exists(path)) GameCatalog.SetExecutable(game, path);
            _settingsService.Save(_settings);
            ShowPage("Games");
        }, margin: new Thickness(0, 0, 0, 12)));
        foreach (var game in GameCatalog.Games)
        {
            var panel = new DockPanel();
            var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(new TextBlock { Text = game.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
            status.Children.Add(Body(game.Status + (game.ExecutablePath is null ? " · locate the installed game to enable launch." : " · " + Path.GetFileName(game.ExecutablePath)), 11));
            DockPanel.SetDock(status, Dock.Left);
            panel.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ActionButton("Locate", () => LocateGame(game), margin: new Thickness(4, 0, 4, 0)));
            buttons.Children.Add(ActionButton("Launch", () => LaunchGame(game, false), accent: true, margin: new Thickness(4, 0, 4, 0), enabled: game.ExecutablePath is not null));
            buttons.Children.Add(ActionButton("Launch with Boost", () => LaunchGame(game, true), margin: new Thickness(4, 0, 4, 0), enabled: game.ExecutablePath is not null));
            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);
            page.Children.Add(Card(game.Status.ToUpperInvariant(), panel, new Thickness(0, 3, 0, 5)));
        }
        return ScrollPage(page);
    }

    private UIElement BuildGameBooster()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Game Booster", "RISE can launch a located game and request a higher process priority. It does not kill background apps, change power plans, promise FPS gains, or claim lower ping."));
        var profiles = new StackPanel();
        foreach (var (name, description) in new[]
        {
            ("Light", "Normal priority. Minimal change; prioritises stability."),
            ("Balanced", "Above-normal priority. Recommended for most systems."),
            ("Extreme", "High priority. May make other applications less responsive; never uses real-time priority.")
        })
        {
            var panel = new DockPanel();
            panel.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold },
                    Body(description, 12)
                }
            });
            var choose = ActionButton(_settings.BoostProfile == name ? "Selected" : "Select", () =>
            {
                _settings.BoostProfile = name;
                _settingsService.Save(_settings);
                ShowPage("Game Booster");
            }, accent: _settings.BoostProfile == name);
            DockPanel.SetDock(choose, Dock.Right);
            panel.Children.Add(choose);
            page.Children.Add(Card(name.ToUpperInvariant(), panel, new Thickness(0, 4, 0, 4)));
        }
        page.Children.Add(SectionTitle("LIVE SESSION"));
        _gameMonitorText = Body(_monitorState, 13);
        page.Children.Add(Card("PROCESS MONITOR", _gameMonitorText, new Thickness(0, 4, 0, 8)));
        page.Children.Add(ActionButton("Open supported games", () => ShowPage("Games")));
        page.Children.Add(Body("FPS, frame time, and per-game GPU telemetry require supported game/driver APIs and are not provided as estimates here. CPU usage is system-wide; memory readings are for the launched process.", 12));
        return ScrollPage(page);
    }

    private UIElement BuildPowerPlans()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Power plans", "Activate only a Windows power scheme that already exists. RISE does not create custom plans or force maximum performance on battery."));
        var output = new StackPanel();
        var activePlan = GetActivePowerPlan();
        output.Children.Add(new TextBlock { Text = "Active scheme", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        output.Children.Add(Body(activePlan.Name, 12));
        output.Children.Add(Body(activePlan.Guid is null ? "Windows did not report an active scheme." : activePlan.Guid, 11));
        page.Children.Add(Card("CURRENT PLAN", output, new Thickness(0, 0, 0, 10)));
        page.Children.Add(ActionButton("Save current plan to restore later", () =>
        {
            _originalPowerPlan = GetActivePowerPlan().Guid;
            MessageBox.Show(_originalPowerPlan is null ? "Windows did not report an active scheme." : "The active scheme is saved for restoration until RISE closes.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
        }, margin: new Thickness(0, 0, 0, 12)));

        var existing = GetAvailablePowerPlans();
        foreach (var plan in WindowsPlans)
        {
            var installed = existing.Any(id => id.Equals(plan.Guid, StringComparison.OrdinalIgnoreCase));
            var panel = new DockPanel();
            panel.Children.Add(Body(plan.Name + (plan.Name == "Power Saver" ? " · suitable for battery use" : string.Empty), 14));
            var activate = ActionButton(installed ? "Activate" : "Not installed", () => ActivatePowerPlan(plan.Name, plan.Guid), enabled: installed);
            DockPanel.SetDock(activate, Dock.Right);
            panel.Children.Add(activate);
            page.Children.Add(Card(installed ? "AVAILABLE IN WINDOWS" : "NOT AVAILABLE ON THIS DEVICE", panel, new Thickness(0, 3, 0, 5)));
        }
        page.Children.Add(ActionButton("Restore saved scheme", RestorePowerPlan, margin: new Thickness(0, 12, 0, 8)));
        return ScrollPage(page);
    }

    private UIElement BuildNetwork()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Network", "Live adapter details and direct connectivity tests. Results depend on your network and endpoint; DNS changes do not guarantee lower game ping."));
        var adapterList = new StackPanel();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = adapter.GetIPProperties();
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = adapter.Name, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(Body($"{adapter.NetworkInterfaceType} · {adapter.Speed / 1_000_000} Mbps · {adapter.Description}", 11));
            panel.Children.Add(Body("Addresses: " + string.Join(", ", props.UnicastAddresses.Select(a => a.Address.ToString())), 11));
            panel.Children.Add(Body("DNS: " + string.Join(", ", props.DnsAddresses.Select(a => a.ToString())), 11));
            adapterList.Children.Add(Card("ACTIVE ADAPTER", panel, new Thickness(0, 4, 0, 4)));
        }
        if (adapterList.Children.Count == 0) adapterList.Children.Add(Body("No active network adapters found.", 12));
        page.Children.Add(adapterList);
        var actions = new StackPanel();
        actions.Children.Add(ActionButton("Test connectivity · 1.1.1.1", RunNetworkTest, accent: true, margin: new Thickness(0, 0, 0, 10)));
        actions.Children.Add(ActionButton("Flush DNS cache", FlushDns, margin: new Thickness(0, 0, 0, 10)));
        actions.Children.Add(ActionButton("Open Windows network settings", () => OpenUrl("ms-settings:network-status")));
        page.Children.Add(Card("DIAGNOSTICS", actions, new Thickness(0, 12, 0, 8)));
        _networkResultsText = Body("No test run yet.", 12);
        page.Children.Add(Card("TEST RESULTS", _networkResultsText, new Thickness(0, 4, 0, 8)));
        page.Children.Add(Body("DNS benchmark and DNS provider switching are not implemented. Packet-loss and latency measurements are from ICMP echo replies to the selected public endpoint only.", 12));
        return ScrollPage(page);
    }

    private UIElement BuildDebloat()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Installed applications", "Review installed desktop applications. RISE does not silently remove Windows components; use Windows' own uninstall flow for optional applications."));
        page.Children.Add(ActionButton("Open Windows installed apps", () => OpenUrl("ms-settings:appsfeatures"), accent: true, margin: new Thickness(0, 0, 0, 12)));
        var apps = ReadInstalledApps();
        page.Children.Add(Body($"{apps.Count} registered desktop apps found. Select uninstall in Windows Settings after reviewing each application.", 12));
        foreach (var app in apps.Take(80))
        {
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = app.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            details.Children.Add(Body(app.Publisher, 11));
            if (!string.IsNullOrWhiteSpace(app.Version)) details.Children.Add(Body("Version " + app.Version, 11));
            page.Children.Add(Card("REGISTERED APP", details, new Thickness(0, 3, 0, 4)));
        }
        return ScrollPage(page);
    }

    private UIElement BuildTools()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Tools", "Windows-native tools and RISE-managed backups. Destructive cleanup is limited to old files directly inside your user temporary folder."));
        page.Children.Add(ActionButton("Clean user temporary files older than 7 days", CleanTempFiles, accent: true, margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Manage current-user startup apps", () => ShowPage("Startup Manager"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Open Windows startup apps", () => OpenUrl("ms-settings:startupapps"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("View installed apps", () => ShowPage("Debloat"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Open Windows recovery / repair settings", () => OpenUrl("ms-settings:recovery"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Export RISE system report", ExportReport, margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Restore all RISE registry tweaks", RestoreAllTweaks, margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Open RISE activity log", OpenActivityLog));
        page.Children.Add(Body("RISE does not modify Windows services, kill processes, or clear game-specific caches. Service and startup changes are available through Windows' own settings.", 12));
        return ScrollPage(page);
    }

    private UIElement BuildStartupManager()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Startup manager", "Review apps registered to run at sign-in for your Windows user. Disable moves an entry into RISE's reversible backup key; restore puts it back. No administrator access is required."));
        page.Children.Add(Body("This view manages only the current user's registry Run entries. Startup folders, scheduled tasks, and entries registered for all users are not changed here. Review each command before disabling it.", 12));
        page.Children.Add(ActionButton("Refresh entries", () => ShowPage("Startup Manager"), margin: new Thickness(0, 10, 0, 10)));
        try
        {
            var entries = _startupManagerService.GetEntries();
            if (entries.Count == 0)
                page.Children.Add(Body("No current-user startup entries were found.", 12));

            foreach (var entry in entries)
            {
                var details = new StackPanel();
                details.Children.Add(new TextBlock { Text = entry.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
                details.Children.Add(Body(entry.Command, 11));
                var panel = new DockPanel();
                panel.Children.Add(details);
                var action = entry.Name.Equals("RISE", StringComparison.OrdinalIgnoreCase)
                    ? ActionButton("Manage in Settings", () => ShowPage("Settings"), margin: new Thickness(8, 0, 0, 0))
                    : ActionButton(entry.Enabled ? "Disable" : "Restore",
                        () => ChangeStartupEntry(entry, !entry.Enabled),
                        accent: entry.Enabled,
                        margin: new Thickness(8, 0, 0, 0));
                DockPanel.SetDock(action, Dock.Right);
                panel.Children.Add(action);
                page.Children.Add(Card(entry.Enabled ? "ENABLED" : "DISABLED · BACKUP SAVED", panel, new Thickness(0, 4, 0, 4)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            page.Children.Add(Body($"RISE could not read startup entries: {ex.Message}", 12));
        }
        return ScrollPage(page);
    }

    private void ChangeStartupEntry(StartupEntry entry, bool enabled)
    {
        var action = enabled ? "restore" : "disable";
        if (MessageBox.Show(
                $"{action} “{entry.Name}” at sign-in?\n\nCommand:\n{entry.Command}\n\nRISE will move this value between current-user registry keys without deleting it.",
                "Confirm startup change",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _startupManagerService.SetEnabled(entry, enabled);
            AppendActivity($"{(enabled ? "Restored" : "Disabled")} current-user startup entry: {entry.Name}.");
            ShowPage("Startup Manager");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException or FormatException)
        {
            MessageBox.Show($"RISE could not update this startup entry.\n\n{ex.Message}", "Startup change failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private UIElement BuildSettings()
    {
        var page = NewPage();
        page.Children.Add(WelcomeBlock("Settings & support", $"RISE FREE · {_tweakService.SupportedFreeCount} supported tweaks · v{GetType().Assembly.GetName().Version?.ToString(3) ?? "0.5.0"} · Settings are stored under your local app data."));

        var accentPanel = new StackPanel();
        accentPanel.Children.Add(new TextBlock { Text = "Accent colour", FontWeight = FontWeights.SemiBold });
        var accentPicker = new ComboBox { Width = 200, Margin = new Thickness(0, 8, 0, 0) };
        accentPicker.Items.Add("Orange");
        accentPicker.Items.Add("Amber");
        accentPicker.Items.Add("Violet");
        accentPicker.Items.Add("Blue");
        accentPicker.SelectedIndex = _settings.Accent switch { "#FFFFB648" => 1, "#FF9B7AFF" => 2, "#FF64A8FF" => 3, _ => 0 };
        accentPicker.SelectionChanged += (_, _) =>
        {
            var color = accentPicker.SelectedIndex switch { 1 => "#FFFFB648", 2 => "#FF9B7AFF", 3 => "#FF64A8FF", _ => "#FFFF762E" };
            _settings.Accent = color;
            ApplyAccent(color);
            SaveSettings();
        };
        accentPanel.Children.Add(accentPicker);
        page.Children.Add(Card("APPEARANCE", accentPanel, new Thickness(0, 0, 0, 8)));

        var startupPanel = new StackPanel();
        var startup = new CheckBox { Content = "Launch RISE when I sign in to Windows", IsChecked = _settings.LaunchAtStartup, Foreground = Brushes.WhiteSmoke };
        startup.Checked += (_, _) => SetLaunchAtStartup(true);
        startup.Unchecked += (_, _) => SetLaunchAtStartup(false);
        startupPanel.Children.Add(startup);
        var animation = new CheckBox { Content = "Play launch animation", IsChecked = _settings.AnimationsEnabled, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 10, 0, 0) };
        animation.Checked += (_, _) => { _settings.AnimationsEnabled = true; SaveSettings(); };
        animation.Unchecked += (_, _) => { _settings.AnimationsEnabled = false; SaveSettings(); };
        startupPanel.Children.Add(animation);
        page.Children.Add(Card("APPLICATION", startupPanel, new Thickness(0, 0, 0, 8)));

        page.Children.Add(ActionButton("RISE Pro information · official website", () => OpenUrl("https://risetweaks.netlify.app/"), accent: true, margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(Body("RISE does not currently have desktop authentication or a subscription-verification backend. The app cannot verify or grant Pro entitlements.", 12));
        page.Children.Add(ActionButton("Discord support & community", () => OpenUrl("https://discord.com/invite/qfqhJRv8m"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(ActionButton("Official website", () => OpenUrl("https://risetweaks.netlify.app/"), margin: new Thickness(0, 0, 0, 8)));
        page.Children.Add(Body($"RISE version {GetType().Assembly.GetName().Version?.ToString(3) ?? "0.5.0"} · no password or account data is stored locally.", 12));
        page.Children.Add(Body("Automatic update checking, tray minimization, account/license validation, and developer diagnostics are not implemented in this build.", 12));
        return ScrollPage(page);
    }

    private void RefreshLiveStatus()
    {
        if (_currentPage == "Home" && _initialAnimationFinished) RefreshHomeMetrics();

        if (_monitoredGame is not null)
        {
            try
            {
                _monitoredGame.Refresh();
                if (_monitoredGame.HasExited)
                {
                    _monitoredGame.Dispose();
                    _monitoredGame = null;
                    _monitorState = $"{_monitoredGameName} exited. The temporary process-priority setting ended with its process.";
                    _monitoredGameName = null;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var cpuNow = _monitoredGame.TotalProcessorTime;
                    var elapsed = (now - _lastProcessSampleAt).TotalSeconds;
                    var cpuPercent = elapsed > 0 && _lastProcessCpuTime.HasValue
                        ? 100 * (cpuNow - _lastProcessCpuTime.Value).TotalSeconds / elapsed
                        : 0;
                    _lastProcessCpuTime = cpuNow;
                    _lastProcessSampleAt = now;
                    _monitorState = $"{_monitoredGameName} is running · PID {_monitoredGame.Id}\nProcess CPU: {Math.Max(0, cpuPercent):0.#}% (one-core scale) · Process working set: {SystemSnapshotService.FormatBytes((ulong)Math.Max(0, _monitoredGame.WorkingSet64))}\nFPS, frame time, per-game GPU, and game network latency: unavailable.";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                _monitoredGame?.Dispose();
                _monitoredGame = null;
                _monitorState = "The game process is no longer available.";
            }
        }
        if (_currentPage == "Game Booster" && _gameMonitorText is not null)
            _gameMonitorText.Text = _monitorState;
    }

    private void RefreshHomeMetrics()
    {
        _snapshot = SystemSnapshotService.Read();
        if (_homeValues.Count != 4 || _homeDetails.Count != 4) return;
        _homeValues[0].Text = _snapshot.Cpu;
        _homeDetails[0].Text = $"{_snapshot.CpuPercent:0}% system use";
        _homeValues[1].Text = $"{_snapshot.RamUsed} / {_snapshot.RamTotal}";
        _homeDetails[1].Text = $"{_snapshot.RamUsedPercent:0}% in use";
        _homeValues[2].Text = _snapshot.DiskFree;
        _homeDetails[2].Text = $"free of {_snapshot.DiskTotal}";
        _homeValues[3].Text = _snapshot.Network;
        _homeDetails[3].Text = _snapshot.Uptime + " uptime";
        var percentages = new[] { _snapshot.CpuPercent, _snapshot.RamUsedPercent, 0d, 0d };
        for (var i = 0; i < _homeBars.Count; i++)
            if (_homeBars[i] is { } bar) bar.Value = Math.Clamp(percentages[i], 0, 100);
    }

    private void ChangeTweak(TweakDefinition tweak, bool apply)
    {
        try
        {
            if (apply)
            {
                var result = MessageBox.Show(
                    $"Apply “{tweak.Name}”?\n\n{tweak.Description}\n\nCatalog risk: {tweak.Risk}\nTier: {tweak.Tier}\nRestart required: {(tweak.RestartRequired ? "yes" : "no")}\n\nRISE will back up the original current-user registry values before changing them.",
                    "Confirm Windows setting",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
                _tweakService.Apply(tweak);
            }
            else
            {
                _tweakService.Restore(tweak);
            }
            ShowPage(_currentPage);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException or FormatException)
        {
            MessageBox.Show($"RISE could not change this setting.\n\n{ex.Message}", "Change failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyTweaks(IReadOnlyCollection<TweakDefinition> tweaks)
    {
        var pending = tweaks.Where(t => _tweakService.CanApply(t) && !_tweakService.IsApplied(t)).ToArray();
        if (pending.Length == 0)
        {
            MessageBox.Show("There are no new supported tweaks to apply. Existing changes can be restored individually or with Restore All.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = "Apply these supported, reversible current-user registry tweaks?\n\n" + string.Join("\n", pending.Select(t => "• " + t.Name)) + "\n\nUnsupported catalog entries are skipped. The original value of every changed setting is backed up first.";
        if (MessageBox.Show(prompt, "Confirm Apply All", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var tweak in pending) _tweakService.Apply(tweak);
            ShowPage(_currentPage);
            MessageBox.Show($"{pending.Length} tweaks applied.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException or FormatException)
        {
            MessageBox.Show($"The operation stopped at the first error. Review the saved backups and activity log.\n\n{ex.Message}", "Optimisation incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyAllCatalogTweaks()
    {
        var supported = _tweakService.Definitions.Where(_tweakService.CanApply).ToArray();
        ApplyTweaks(supported);
    }

    private void RestoreAllTweaks()
    {
        var saved = _tweakService.Definitions.Where(_tweakService.HasBackup).ToArray();
        if (saved.Length == 0)
        {
            MessageBox.Show("No RISE-managed tweaks are currently applied.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Restore {saved.Length} RISE-managed tweaks to their saved original values?", "Restore RISE settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var tweak in saved) _tweakService.Restore(tweak);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            MessageBox.Show($"Restore stopped at the first error. Review the backups and activity log.\n\n{ex.Message}", "Restore incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        ShowPage(_currentPage);
    }

    private void LocateGame(GameEntry game)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate {game.Name} executable",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            GameCatalog.SetExecutable(game, dialog.FileName);
            _settings.GamePaths[game.Name] = dialog.FileName;
            SaveSettings();
            ShowPage("Games");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Could not locate game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LaunchGame(GameEntry game, bool boost)
    {
        var priority = !boost || _settings.BoostProfile == "Light"
            ? ProcessPriorityClass.Normal
            : _settings.BoostProfile == "Extreme" ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
        try
        {
            var process = GameCatalog.Launch(game);
            _monitoredGame = process;
            _monitoredGameName = game.Name;
            _lastProcessCpuTime = process.TotalProcessorTime;
            _lastProcessSampleAt = DateTime.UtcNow;
            _monitorState = $"{game.Name} launched by RISE · PID {process.Id} · requested {priority} priority.\nSystem CPU: live on Home · game GPU, FPS, and frame-time telemetry: unavailable.";
            AppendActivity($"Launched {game.Name} (PID {process.Id}), profile {(boost ? _settings.BoostProfile : "Unboosted")}.");
            if (_currentPage != "Game Booster") ShowPage("Game Booster");
            await Task.Delay(100);
            if (!process.HasExited)
            {
                try { process.PriorityClass = priority; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    _monitorState = $"{game.Name} launched, but Windows did not permit changing its process priority. No other changes were made.";
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Game could not be launched", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static (string? Guid, string Name) GetActivePowerPlan()
    {
        var result = RunProcess("powercfg.exe", "/getactivescheme");
        if (result.ExitCode != 0) return (null, "Unavailable");
        var guid = ExtractGuid(result.Output);
        var name = result.Output[(result.Output.LastIndexOf(':') + 1)..].Trim();
        return (guid, name);
    }

    private static HashSet<string> GetAvailablePowerPlans()
    {
        var result = RunProcess("powercfg.exe", "/list");
        if (result.ExitCode != 0) return [];
        return System.Text.RegularExpressions.Regex.Matches(result.Output, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")
            .Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ActivatePowerPlan(string name, string guid)
    {
        if (MessageBox.Show($"Activate the existing Windows “{name}” power plan?\n\nThis changes the active system power plan. It can be restored from this page while RISE is open.", "Change active power plan", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_originalPowerPlan is null) _originalPowerPlan = GetActivePowerPlan().Guid;
        var result = RunProcess("powercfg.exe", $"/setactive {guid}");
        if (result.ExitCode != 0)
            MessageBox.Show(result.Output.Trim(), "Could not activate power plan", MessageBoxButton.OK, MessageBoxImage.Error);
        else
        {
            AppendActivity($"Activated existing Windows power plan {name}.");
            ShowPage("Power Plans");
        }
    }

    private void RestorePowerPlan()
    {
        if (_originalPowerPlan is null)
        {
            MessageBox.Show("No original power plan has been saved in this RISE session.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = RunProcess("powercfg.exe", $"/setactive {_originalPowerPlan}");
        if (result.ExitCode != 0) MessageBox.Show(result.Output.Trim(), "Could not restore power plan", MessageBoxButton.OK, MessageBoxImage.Error);
        else
        {
            _originalPowerPlan = null;
            ShowPage("Power Plans");
        }
    }

    private async void RunNetworkTest()
    {
        if (_networkResultsText is not null) _networkResultsText.Text = "Testing 1.1.1.1 (ICMP), 4 attempts…";
        long total = 0;
        var received = 0;
        var details = new List<string>();
        try
        {
            using var ping = new Ping();
            for (var i = 0; i < 4; i++)
            {
                var reply = await ping.SendPingAsync("1.1.1.1", 1500);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    total += reply.RoundtripTime;
                    details.Add($"{reply.RoundtripTime} ms");
                }
                else details.Add(reply.Status.ToString());
            }
            var average = received == 0 ? "n/a" : $"{total / (double)received:0.#} ms";
            var summary = $"1.1.1.1 · {received}/4 replies · {4 - received}/4 lost · average RTT {average}";
            if (_networkResultsText is not null) _networkResultsText.Text = summary + "\n" + string.Join(" · ", details);
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            if (_networkResultsText is not null) _networkResultsText.Text = $"Test failed: {ex.Message}";
        }
    }

    private async void FlushDns()
    {
        if (MessageBox.Show("Flush the Windows DNS resolver cache for this device?", "Confirm DNS flush", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var result = await RunProcessAsync("ipconfig.exe", "/flushdns");
            MessageBox.Show(result.ExitCode == 0 ? result.Output : $"Windows returned exit code {result.ExitCode}.\n{result.Output}", "DNS cache", MessageBoxButton.OK, result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            MessageBox.Show(ex.Message, "DNS flush failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CleanTempFiles()
    {
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (MessageBox.Show($"Remove files older than 7 days directly inside this user temporary folder?\n\n{tempPath}\n\nSubfolders, newer files, and locked files will be left alone.", "Clean temporary files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deleted = 0;
        var skipped = 0;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var path in Directory.EnumerateFiles(tempPath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
            AppendActivity($"Temporary file cleanup removed {deleted} files; {skipped} locked or inaccessible files skipped.");
            MessageBox.Show($"Removed {deleted} files. Skipped {skipped} locked or inaccessible files.", "Cleanup complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Cleanup stopped: {ex.Message}", "Cleanup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<InstalledApp> ReadInstalledApps()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subKeyName);
                    if (entry is null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || entry.GetValue("SystemComponent") is int systemComponent && systemComponent == 1) continue;
                    apps.TryAdd(name, new InstalledApp(name, entry.GetValue("Publisher")?.ToString() ?? "Publisher not listed", entry.GetValue("DisplayVersion")?.ToString() ?? ""));
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void SetLaunchAtStartup(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Windows did not report the RISE executable path.");
                run.SetValue("RISE", $"\"{executable}\"");
            }
            else run.DeleteValue("RISE", throwOnMissingValue: false);
            _settings.LaunchAtStartup = enabled;
            SaveSettings();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "Startup setting failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportReport()
    {
        var dialog = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = $"RISE-report-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var snapshot = SystemSnapshotService.Read();
            var report = new StringBuilder()
                .AppendLine("RISE System Report")
                .AppendLine($"Generated: {DateTimeOffset.Now:O}")
                .AppendLine($"Windows: {snapshot.Windows} (build {snapshot.Build})")
                .AppendLine($"CPU: {snapshot.Cpu}")
                .AppendLine($"GPU: {snapshot.Gpu}")
                .AppendLine($"RAM: {snapshot.RamUsed} / {snapshot.RamTotal} ({snapshot.RamUsedPercent:0}% used)")
                .AppendLine($"System drive free: {snapshot.DiskFree} / {snapshot.DiskTotal}")
                .AppendLine($"Architecture: {snapshot.Architecture}")
                .AppendLine($"Form factor: {snapshot.FormFactor}")
                .AppendLine($"Battery: {snapshot.Battery}")
                .AppendLine($"Active power plan: {snapshot.PowerPlan}")
                .AppendLine($"Network: {snapshot.Network}")
                .AppendLine()
                .AppendLine("RISE-managed tweak status:")
                .AppendJoin(Environment.NewLine, _tweakService.Definitions.Select(t => $"- {t.Name}: {_tweakService.GetStatus(t)}"))
                .AppendLine()
                .AppendLine("Game detection:")
                .AppendJoin(Environment.NewLine, GameCatalog.Games.Select(g => $"- {g.Name}: {g.Status}"));
            File.WriteAllText(dialog.FileName, report.ToString());
            MessageBox.Show("The system report was exported.", "RISE", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Report export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenActivityLog()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RISE", "activity.log");
            if (!File.Exists(path)) File.WriteAllText(path, "RISE activity log" + Environment.NewLine);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Activity log unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? ExtractGuid(string text) =>
        System.Text.RegularExpressions.Regex.Match(text, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}").Value is { Length: > 0 } guid ? guid : null;

    private static (int ExitCode, string Output) RunProcess(string executable, string arguments)
    {
        var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string executable, string arguments)
    {
        var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Windows could not open this link.\n\n{url}\n\n{ex.Message}", "Could not open link", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Website_Click(object sender, RoutedEventArgs e) => OpenUrl("https://risetweaks.netlify.app/");
    private void SaveSettings() => _settingsService.Save(_settings);

    private void ApplyAccent(string value)
    {
        if (ColorConverter.ConvertFromString(value) is not Color color) return;
        Application.Current.Resources["AccentColor"] = color;
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
    }

    private static void AppendActivity(string message)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RISE");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "activity.log"), $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
    }

    private static StackPanel NewPage() => new() { Margin = new Thickness(28, 22, 28, 26) };

    private static ScrollViewer ScrollPage(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private UIElement WelcomeBlock(string title, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Body(description, 12));
        return panel;
    }

    private UIElement CreateMetricCard(string title, string value, string detail, double fraction)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 9, Foreground = (Brush)FindResource("MutedBrush") });
        var valueText = new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis };
        var detailText = Body(detail, 11);
        panel.Children.Add(valueText);
        panel.Children.Add(detailText);
        ProgressBar? bar = null;
        if (fraction > 0)
        {
            bar = new ProgressBar { Value = Math.Clamp(fraction * 100, 0, 100), Maximum = 100, Height = 4, Margin = new Thickness(0, 12, 0, 0), Foreground = (Brush)FindResource("AccentBrush"), Background = new SolidColorBrush(Color.FromRgb(45, 46, 52)), BorderThickness = new Thickness(0) };
            panel.Children.Add(bar);
        }
        _homeValues.Add(valueText);
        _homeDetails.Add(detailText);
        _homeBars.Add(bar);
        return Card(title, panel, new Thickness(4, 0, 4, 8), noHeader: true);
    }

    private UIElement Card(string title, UIElement content, Thickness margin, bool noHeader = false)
    {
        var panel = new StackPanel();
        if (!noHeader)
            panel.Children.Add(new TextBlock { Text = title, FontSize = 9, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(content);
        return new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(15),
            Margin = margin,
            Child = panel
        };
    }

    private static TextBlock Body(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = new SolidColorBrush(Color.FromRgb(164, 166, 173)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 0)
    };

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindResource("AccentBrush"),
        Margin = new Thickness(2, 8, 0, 5)
    };

    private static void AddDetail(Panel panel, string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        row.Children.Add(Body(label, 11));
        var text = new TextBlock { Text = value, Foreground = Brushes.WhiteSmoke, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(text);
        panel.Children.Add(row);
    }

    private Button ActionButton(string text, Action action, bool accent = false, Thickness? margin = null, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            Style = accent ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button)),
            Margin = margin ?? new Thickness(0, 0, 0, 0),
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => action();
        return button;
    }

    private sealed record InstalledApp(string Name, string Publisher, string Version);
}
