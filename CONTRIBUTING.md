# Contributing

Thanks for helping improve RISE. Contributions should be safe, honest about their effects, and compatible with Windows 10 and 11.

## Before opening a pull request

- Open an issue for larger features or behavior changes so the approach can be discussed.
- Keep changes focused and explain user-visible behavior, limitations, and rollback behavior.
- Do not add tweaks that disable security protections or essential Windows services.
- Back up original settings before writing them and provide a clear restore path.
- Do not present unavailable FPS, GPU, ping, or performance values as measurements.
- Do not include real credentials, license keys, personal data, or machine-specific logs.

## Build and verify

On Windows with the .NET 8 SDK:

```powershell
dotnet build RISE.csproj -c Release
dotnet run --project RISE.csproj
```

Check that the affected pages open, actions report errors clearly, and reversible changes can be restored. GitHub Actions also builds the Windows application for pushes and pull requests.

## Pull requests

Include a concise description, the affected areas, and the verification you ran. Update the README when changing user-visible functionality or known limitations.
