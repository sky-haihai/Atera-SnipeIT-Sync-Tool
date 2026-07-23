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

## Windows x64 MSI release

Version `1.0.0` is published as an unsigned, self-contained Windows x64 MSI. Target machines do not need a preinstalled .NET runtime. Product publisher/company metadata is `Vue IT Inc.`.

Development verification may use a dirty working tree:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1 -AllowDirty
```

The final artifact must be rebuilt from a clean commit without `-AllowDirty`. WiX 7 requires the project owner to review and explicitly accept the applicable OSMF EULA; the repository must not record acceptance until the owner authorizes it.

Expected outputs:

```text
artifacts\release\v1.0.0\AteraSnipeSync-1.0.0-win-x64.msi
artifacts\release\v1.0.0\AteraSnipeSync-1.0.0-win-x64.msi.sha256
artifacts\release\v1.0.0\release-manifest.json
```

Install from an elevated terminal:

```powershell
msiexec.exe /i .\AteraSnipeSync-1.0.0-win-x64.msi /norestart
```

Interactive uninstall asks whether to remove `%ProgramData%\AteraSnipeSync`; the checkbox is clear by default. Silent uninstall also preserves data unless deletion is explicitly requested:

```powershell
# Preserve local configuration, credentials, logs and history.
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart

# Delete the entire local data root during a true uninstall only.
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart REMOVELOCALDATA=1
```

Major upgrades and repairs never delete local data, even if `REMOVELOCALDATA=1` is supplied. See `docs/test-guides/09-Installer-单元测试指导手册.md` for the Windows 11 / Windows Server 2022 acceptance matrix.

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
