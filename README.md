# AteraSnipeSync

A modular .NET sync system that imports Atera managed devices into Snipe-IT.

## Projects

- `AteraSnipeSync.Core`
- `AteraSnipeSync.WorkerService`
- `AteraSnipeSync.TrayApp`
- `AteraSnipeSync.Tests`

## Build

```powershell
dotnet build
```

## Test

```powershell
dotnet test
```

## Local Config

Use the TrayApp to enter and save the Atera API key locally:

```powershell
dotnet run --project .\src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj
```

The app saves local settings to:

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

You can also copy the template manually:

```text
samples/configs/appsettings.local.example.json
```

to:

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

Do not commit real API keys or tokens.
