## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `dotnet build RISE.csproj -c Release`
- [ ] Manually checked the affected page or Windows behavior
- [ ] Updated the README or documented why it is not needed

## Safety review

- [ ] System changes have a clear explanation and rollback path
- [ ] No security protections or essential Windows services are disabled
- [ ] No performance or telemetry claims are fabricated
- [ ] No credentials, personal data, or machine-specific files are included
