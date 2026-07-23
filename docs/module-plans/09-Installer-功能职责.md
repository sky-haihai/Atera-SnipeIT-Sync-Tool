# Installer - 功能职责

## 1. 模块目标

Installer 模块负责把已经验证的 AteraSnipeSync WorkerService 与 TrayApp 发布为可追溯、可升级、可卸载的 Windows x64 MSI。目标环境是 Windows 11 x64 与 Windows Server 2022 x64；目标机器不需要预装 .NET 10。

本模块只负责发布、安装、升级、修复和卸载边界，不改变 Atera、Snipe-IT、mapping、scheduler、notification 或 IPC 的业务协议。

## 2. 输入

- clean 或显式允许 dirty 的 Git 工作树。
- `Version`，v1.0 首发固定为 `1.0.0`。
- Release 配置的 WorkerService 与 TrayApp 项目。
- 固定的产品 identity、service identity、安装路径和 ProgramData 路径。
- WiX 7 SDK、UI extension 与 Util extension。
- project owner 对 WiX 7 OSMF EULA 的明确审阅/接受；Installer module 或 automation 不代表 owner 接受法律条款。

## 3. 输出

发布命令必须生成：

- `AteraSnipeSync-1.0.0-win-x64.msi`
- `AteraSnipeSync-1.0.0-win-x64.msi.sha256`
- `release-manifest.json`

manifest 至少记录 version、RID、self-contained、Git commit、dirty 状态、MSI 文件名、SHA-256、Manufacturer 和签名状态。manifest 不得包含 API key、token、SMTP password、webhook URL 或 local config 内容。

## 4. 安装职责

Installer 必须：

- per-machine 安装到 `%ProgramFiles%\AteraSnipeSync`。
- 把 self-contained Worker 与 Tray 文件放在同一目录。
- 注册 `AteraSnipeItAutoSync` Windows Service，显示名 `Atera Snipe-IT Auto Sync`，LocalSystem、自动启动。
- 安装或升级后启动 Worker；升级或卸载前停止 Worker；卸载时删除 service registration。
- 创建所有用户 Start Menu shortcut。
- 写入 HKLM `Run` 值 `AteraSnipeSync.TrayApp`，使每个交互式登录会话启动 Tray。
- 创建 `%ProgramData%\AteraSnipeSync`，SYSTEM/Administrators 拥有 FullControl，Builtin Users 拥有可继承 Modify 权限。
- 在 Add/Remove Programs 显示 Product `Atera Snipe-IT Auto Sync` 和 Publisher `Vue IT Inc.`。

Installer 不得安装 `appsettings.local.json`、示例 credential、development settings、PDB、test assembly 或 source file。

## 5. 升级和修复职责

- UpgradeCode 固定为 `549B4FDF-C466-4CF0-A356-0EC6380C24CD`。
- ProductCode 从固定 namespace `AD4D8FDE-7A95-4D4E-8A44-988FAE44D807` 和三段 numeric version 确定性生成；同一 version 不得因为重建改变 ProductCode。
- Major Upgrade 必须保留 ProgramData、阻止 downgrade，并停止旧 Worker 后再替换文件。
- repair 不得重写、删除或创建 local config，也不得删除 logs/history/preflight/schedule state。

## 6. 卸载和本地数据职责

交互式完整卸载必须显示 `RemoveLocalDataDlg`：

- 只在 `Installed AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE` 时显示。
- 明确显示 `%ProgramData%\AteraSnipeSync`。
- checkbox 默认不勾选。
- checkbox 表达删除 config、plaintext credentials、Logs、History、Preflight 和 schedule state。

公开 MSI property 是 `REMOVELOCALDATA`：

- unset、空值或非 `1`：保留整个 ProgramData root。
- `1`：仅在真正卸载且不是 upgrade 时递归删除整个 ProgramData root。

静默卸载默认保留；管理员必须显式传入 `REMOVELOCALDATA=1` 才允许删除。取消卸载不得删除程序或数据。Upgrade 即使收到该 property 也不得删除 ProgramData。

路径必须在安装时写入 HKLM，并在卸载的 `CostInitialize` 前恢复到 MSI property；目录清理使用 WiX Util `RemoveFolderEx` 的 condition，不执行 PowerShell、cmd 或任意外部删除脚本。

## 7. 发布构建职责

发布脚本必须：

- 正式模式拒绝 dirty Git working tree；`-AllowDirty` 只供开发验证。
- 先通过 Release build/test，再运行两个 project-level self-contained publish。
- 使用 `win-x64`、`SelfContained=true`、`PublishSingleFile=false`、`PublishTrimmed=false`。
- 在独立 staging folder 合并两个 publish output；collision 只有 SHA-256 相同才允许。
- 明确拒绝 PDB、`appsettings.Development.json`、`appsettings.local.json`、`*.local.json` 和 test artifacts。
- 构建 MSI、计算 SHA-256，并原子写入 release manifest。
- 任一步失败时返回非零 exit code，不留下看似成功的 final artifact set。

## 8. 成功条件

- Release build 0 warnings / 0 errors。
- 完整 automated tests 一次通过；已知 IPC timing test 连续 20 次通过。
- WiX build 和 MSI validation 通过。
- administrative extraction 证明 Worker/Tray EXE 同目录，版本和 Company 正确，没有禁止文件。
- contract tests 锁定 service、startup、ACL、upgrade、Manufacturer、uninstall prompt 和 conditional data removal。
- Windows 11 / Server 2022 隔离 VM 手工验证 install/repair/upgrade/uninstall matrix。

## 9. 失败条件

以下任一情况阻止 v1.0 tag：

- source build/test 或 WiX validation 失败。
- 若 owner 于 2026-07-23 授权记录的 `AcceptEula=wix7` 缺失或错误，WiX 7 返回 `WIX7015`，release 必须失败。
- dirty source 生成了被标记为 final 的 artifact。
- MSI 包含 credential/local config/development/test/PDB 文件。
- Worker/Tray 不在同一目录，service identity/account/start mode 不符合固定 contract。
- interactive uninstall 默认删除数据，silent uninstall 无 property 删除数据，或 upgrade 删除 ProgramData。
- MSI hash/manifest 与实际 artifact 不一致。

## 10. 不属于本模块的职责

- Atera/Snipe-IT API 或 wire contract 变更。
- credential encryption、DPAPI 或 secret vault migration。
- x86、ARM64、MSIX、portable ZIP、auto-update、online bootstrapper。
- public distribution、code signing certificate procurement 或 website/support URL。
- 在 automated tests 中安装/删除真实 Windows Service 或调用真实外部 API。

## 11. 扩展点

- code signing 与 timestamp server。
- ARM64 artifact。
- managed deployment metadata（Intune/winget）。
- credential protection migration。
- installer localization 和 support URL。
