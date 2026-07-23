# Installer - 技术规格

## 1. 目标和文件

本规格基于 `docs/module-plans/09-Installer-功能职责.md`。实现创建：

- `installer/AteraSnipeSync.Installer/AteraSnipeSync.Installer.wixproj`
- `installer/AteraSnipeSync.Installer/Package.wxs`
- `installer/AteraSnipeSync.Installer/RemoveLocalDataDlg.wxs`
- `scripts/Build-Release.ps1`
- `tests/AteraSnipeSync.Tests/Installer/InstallerContractTests.cs`

现有 `Directory.Build.props` 提供统一 version/company metadata；Worker project 明确排除 development settings 的 publish。

## 2. 固定 identity

```text
ProductName:       Atera Snipe-IT Auto Sync
Manufacturer:      Vue IT Inc.
Version:           1.0.0
Platform:          x64
InstallScope:      perMachine
InstallFolder:     %ProgramFiles%\AteraSnipeSync
ProgramDataRoot:   %ProgramData%\AteraSnipeSync
UpgradeCode:       549B4FDF-C466-4CF0-A356-0EC6380C24CD
ProductNamespace:  AD4D8FDE-7A95-4D4E-8A44-988FAE44D807
ServiceName:       AteraSnipeItAutoSync
ServiceDisplay:    Atera Snipe-IT Auto Sync
ServiceAccount:    LocalSystem
StartupValueName:  AteraSnipeSync.TrayApp
```

ProductCode 由 release script 使用 RFC 4122 UUID v5 算法，以 ProductNamespace 为 namespace、规范化 `major.minor.patch` 为 name 生成，并通过 MSBuild property `ProductCode` 传给 WiX。script 拒绝 prerelease/build metadata 和非三段 numeric version。

## 3. Project 和 build properties

`AteraSnipeSync.Installer.wixproj`：

- SDK `WixToolset.Sdk/7.0.0`。
- PackageReference `WixToolset.UI.wixext/7.0.0` 与 `WixToolset.Util.wixext/7.0.0`。
- WiX 7 OSMF EULA 必须由 project owner 阅读后明确接受；repository/agent 不得预先写入 `AcceptEula` 或代表 owner 接受。未获授权时 `WIX7015` 是 release blocker。
- `Platform=x64`、`OutputType=Package`、`SuppressValidation=false`。
- 使用 bind path/MSBuild property `PublishRoot` 指向 merged staging root。
- build 必须收到 `Version`、`ProductCode`、`PublishRoot`、`OutputPath`；缺失时 fail fast。
- project 不加入 `AteraSnipeSync.sln`，避免日常 build 自动 publish/packaging。

`Directory.Build.props` 增加：

```xml
<VersionPrefix>1.0.0</VersionPrefix>
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
<Company>Vue IT Inc.</Company>
<Product>Atera Snipe-IT Auto Sync</Product>
```

WorkerService 与 TrayApp 都声明 `RuntimeIdentifiers=win-x64` 和 `RuntimeFrameworkVersion=10.0.10`。WorkerService 在 publish framework resolution 中启用 Windows Forms/WindowsDesktop shared framework，使两个 self-contained app 使用同一个 WindowsDesktop runtime payload；这不改变 Worker UI 或 service behavior。两个 publish 的任何同 relative path file 必须 byte-identical，否则 merge fail。该统一基线同时避免 Core runtime/Desktop runtime facade 和 EventLog patch level 被静默覆盖。

## 4. Publish 和 staging

`Build-Release.ps1` public parameters：

```powershell
param(
    [string]$Version = '1.0.0',
    [switch]$AllowDirty
)
```

脚本使用 `$PSScriptRoot` 解析 repository root，设置 `$ErrorActionPreference = 'Stop'`，并执行：

1. 验证 Windows、version、Git state 和 required project files。
2. 对 solution 和 installer project 执行 `dotnet restore --locked-mode`。
3. `dotnet build AteraSnipeSync.sln -c Release --no-restore`。
4. `dotnet test AteraSnipeSync.sln -c Release --no-build --no-restore`。
5. project-level `dotnet publish` 到两个独立 temporary directories。
6. 按 relative path 合并到 staging；collision 使用 `Get-FileHash -Algorithm SHA256` 比较。
7. 拒绝 forbidden file patterns，并验证两个固定 EXE 位于 staging root。
8. 计算 UUID v5 ProductCode。
9. `dotnet build` WiX project，传入 `Version`、`ProductCode`、`PublishRoot` 和 `ReleaseOutputRoot`。
10. 验证 MSI 存在，写入 lowercase SHA-256 line 和 JSON manifest。

Publish properties：

```text
Configuration=Release
RuntimeIdentifier=win-x64
SelfContained=true
PublishSingleFile=false
PublishTrimmed=false
DebugSymbols=false
DebugType=None
```

脚本清理/重建的范围仅限 ignored `artifacts/release/<version>` 和 `artifacts/.staging/<version>`；不得删除 workspace root、source、ProgramData 或安装目录。

## 5. WiX package structure

`Package.wxs` 使用 v4 namespace 与 util namespace。`Package` 必须包含 Manufacturer、Version、ProductCode、UpgradeCode、Scope、Platform、compressed media 和 `MajorUpgrade DowngradeErrorMessage=...`。

Files：

- `INSTALLFOLDER` 使用 `ProgramFiles64Folder`。
- `Files Include="!(bindpath.PublishRoot)\**"` harvest merged output。
- 明确 Exclude `*.pdb`、`appsettings.Development.json`、`appsettings.local.json`、`*.local.json`、`*Tests*`。
- Worker EXE 使用显式 `File`/`Component`，作为 service component KeyPath；harvest set 必须排除该 EXE，避免 duplicate file row。
- Tray EXE 使用显式 `File`/`Component`，承载 Start Menu shortcut 与 ARP icon source；harvest set 排除该 EXE。

Service component：

```text
ServiceInstall:
  Name=AteraSnipeItAutoSync
  DisplayName=Atera Snipe-IT Auto Sync
  Type=ownProcess
  Start=auto
  ErrorControl=normal
  Account=LocalSystem
  Vital=yes

ServiceControl:
  Start=install
  Stop=both
  Remove=uninstall
  Wait=yes
```

不配置 recovery policy、不调用 `sc.exe` custom action。

## 6. Startup、shortcut 和 ProgramData ACL

Tray component 写入：

```text
HKLM\Software\Microsoft\Windows\CurrentVersion\Run
Name=AteraSnipeSync.TrayApp
Value="[INSTALLFOLDER]AteraSnipeSync.TrayApp.exe"
```

Start Menu shortcut 位于 Common Programs 下的 `Atera Snipe-IT Auto Sync` folder，目标为 Tray EXE，working directory 为 INSTALLFOLDER。卸载时 shortcut 和 empty folder 都删除。

ProgramData component：

- `CreateFolder` 创建 root、Logs、History、Preflight。
- util `PermissionEx`：SYSTEM/Administrators `GenericAll=yes`；Builtin Users 使用 read/write/append/create/delete-child/traverse 权限并 `Inheritable=yes`，等价于 Modify。
- component 使用 machine registry value 作为 KeyPath并保持永久，以保证默认卸载保留 ProgramData ACL/data。

## 7. Remembered path 和 conditional removal

安装时写入：

```text
HKLM\Software\Vue IT Inc.\AteraSnipeSync
ProgramDataRoot = [CommonAppDataFolder]AteraSnipeSync
```

卸载时通过 RegistrySearch 在 AppSearch 阶段恢复 secure public property `ATERASNIPESYNC_PROGRAMDATA_ROOT`，保证在 `CostInitialize` 前可用。

always-installed cleanup component 包含：

```xml
<util:RemoveFolderEx
    Property="ATERASNIPESYNC_PROGRAMDATA_ROOT"
    On="uninstall"
    Condition="REMOVELOCALDATA=1 AND REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE" />
```

`REMOVELOCALDATA` 是 Secure public property，默认无值。非 `1` 不得 schedule any recursive removal。Cleanup component 本身随产品正常安装/卸载；remembered path registry value 在普通卸载末尾移除，但 ProgramData data component 为 permanent。

## 8. RemoveLocalDataDlg

`RemoveLocalDataDlg.wxs` 定义 `Dialog Id="RemoveLocalDataDlg"`：

- Title：`Remove local data?`
- 说明卸载程序默认保留 `%ProgramData%\AteraSnipeSync`。
- checkbox property `REMOVELOCALDATA`、CheckBoxValue `1`，初始 property unset。
- checkbox label：`Delete local configuration, credentials, logs, history, preview files, and schedule state.`
- Back 返回 maintenance confirmation；Next 进入 VerifyReady/Progress；Cancel 使用 standard SpawnDialog `CancelDlg`。

自定义 UI 只在 full/basic interactive maintenance remove flow 进入该 dialog，条件固定为：

```text
Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE
```

silent `/qn` 不执行 UI sequence，execute sequence直接根据 command-line property 决定。Upgrade UI 不显示该 dialog。UI 不使用 ExitDlg optional checkbox，因为官方 WixUI 只在 initial install 显示该 control。

## 9. Restart Manager 和运行中 Tray

启用 Windows Installer Restart Manager，不编写 kill-process custom action。若 Tray 文件被使用，MSI 使用标准 files-in-use/restart handling；无法安全完成时返回 restart-required 或 failure，不能记录为 local data 已完整删除。自动测试不终止真实 Tray process。

## 10. Automated tests

`InstallerContractTests` 是带 role comment 的 xUnit class，使用 repository-relative read-only file loading 和 XML parsing，验证：

- version/company/product metadata。
- WiX package identity、UpgradeCode、x64/perMachine、MajorUpgrade downgrade policy。
- service install/control exact values。
- Worker/Tray co-location authoring、HKLM Run、common Start Menu shortcut。
- ProgramData folders、ACL、remembered path。
- `REMOVELOCALDATA` secure/unset default、dialog checkbox、full data wording。
- RemoveFolderEx exact Property/On/Condition，包含 `NOT UPGRADINGPRODUCTCODE`。
- publish script self-contained/non-trimmed/no-symbol properties、clean-tree gate、hash collision gate、forbidden patterns和 artifact names。

IPC regression test修改仅限 test writer lifetime。新增 focused command 连续运行 20 次；automated tests 不注册/删除 service，不执行 MSI，不调用外部 API。

## 11. Artifact verification

发布脚本完成后执行 read-only/administrative checks：

- SHA-256 file 与 `Get-FileHash` 一致。
- manifest version/RID/commit/dirty/hash/manufacturer 正确。
- administrative extraction 到 temporary folder；两个 EXE同目录。
- `FileVersionInfo` 验证 ProductVersion/FileVersion/CompanyName。
- 递归拒绝 forbidden files。

## 12. Manual acceptance

在 disposable Windows 11 x64 与 Windows Server 2022 x64 VM：

1. clean interactive install；检查 files/service/account/start mode/ARP/startup/shortcut/ACL。
2. 无 config 启动；service 保持运行、Tray 能连接 IPC。
3. 创建无敏感信息 marker files 覆盖 config、Logs、History、Preflight、schedule-state。
4. interactive uninstall 默认 unchecked；确认 ProgramData 全保留。
5. reinstall，interactive check delete；确认整个 root 删除。
6. cancel uninstall；确认 product/data 均不变。
7. silent uninstall 无 property；确认保留。
8. silent uninstall `REMOVELOCALDATA=1`；确认全删。
9. repair；确认不重写/删除 data。
10. 使用临时 1.0.1 package 做 Major Upgrade；即使传入 property 也确认 data 保留且 downgrade blocked。
11. 多用户登录验证每 session 一份 Tray，并验证卸载 files-in-use/restart experience。

手工验收不需要也不得输出真实 API key/token；不调用真实 Atera/Snipe-IT/SMTP/webhook。

## 13. Release gate

完成 automated verification并更新 `progress.md` 后，审查 status，只 stage v1.0 files，commit message `release: prepare v1.0.0`。从 clean commit重建 final MSI。仅在两套 VM matrix通过后创建 annotated tag `v1.0.0`；不自动 push。
