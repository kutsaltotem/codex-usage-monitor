# Product images

These images are rendered from the actual WPF controls using synthetic quota and token snapshots. No credentials, account identifiers, real logs or network calls are used. The quota, history and taskbar images are not browser mockups.

Regenerate on Windows from the repository root:

```powershell
dotnet run --project tools/ProductScreenshots/ProductScreenshots.csproj -c Release
```

The exporter also checks that two 100% value capsules fit within each fixed-width indicator group. The UI uses Turkish. Sample values must remain clearly labeled when these images are shared.
