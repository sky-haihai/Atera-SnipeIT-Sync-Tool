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

Copy:

```text
samples/configs/appsettings.local.example.json
```

to:

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

Do not commit real API keys or tokens.
