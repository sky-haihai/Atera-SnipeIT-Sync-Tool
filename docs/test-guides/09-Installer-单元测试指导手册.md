# 09 Installer 单元测试指导手册

## 1. 测试边界

Installer 自动测试只读取 release metadata、WiX source、project 和 PowerShell source，不安装 MSI、不注册/停止/删除真实 Windows Service、不修改 HKLM、不启动 Tray，也不调用 Atera、Snipe-IT、SMTP 或 webhook。

真实安装、repair、upgrade、Restart Manager 和卸载数据行为必须在 disposable Windows 11 x64 / Windows Server 2022 x64 VM 中人工验收。marker 文件不得包含 token、密码、客户名或其他敏感内容。

## 2. 自动化覆盖

`tests/AteraSnipeSync.Tests/Installer/InstallerContractTests.cs` 锁定：

- ProductVersion `1.0.0`、Assembly/FileVersion `1.0.0.0`、Company/Manufacturer `Vue IT Inc.`。
- per-machine x64、UpgradeCode 和 build-time deterministic ProductCode input。
- `AteraSnipeItAutoSync` 的 LocalSystem/automatic/start/stop/remove contract。
- all-users Start Menu shortcut、HKLM Tray Run registration。
- ProgramData root ACL 及 Logs/History/Preflight directory components。
- `REMOVELOCALDATA` secure、默认无值，以及 checkbox `CheckBoxValue=1`。
- `RemoveFolderEx` 的 exact property/on/condition，尤其 `NOT UPGRADINGPRODUCTCODE`。
- release script 的 clean-tree gate、self-contained/non-single-file/non-trimmed publish、hash collision gate、SHA-256 与 manifest names。
- PDB、development/local config 的 source exclusion。

`WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout` 同时回归测试-only request writer lifecycle。请求直接发送一行 UTF-8 bytes 并显式 flush，不再在 server 关闭 pipe 后由 `StreamWriter.Dispose` 二次 flush。handler 执行 750 ms、request read timeout 500 ms，仍证明 command execution 不受 request-read timeout 限制，同时避免 50 ms scheduler window 造成假失败。

## 3. 自动测试命令

先恢复并编译：

```powershell
dotnet restore .\AteraSnipeSync.sln
dotnet restore .\installer\AteraSnipeSync.Installer\AteraSnipeSync.Installer.wixproj
dotnet build .\AteraSnipeSync.sln --configuration Release --no-restore
```

Installer contract：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~InstallerContractTests"
```

Named Pipe flaky gate 连续 20 次：

```powershell
$testName = 'FullyQualifiedName=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout'
1..20 | ForEach-Object {
  dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj `
    --configuration Release --no-build --no-restore `
    --filter $testName
  if ($LASTEXITCODE -ne 0) { throw "IPC regression failed on run $_" }
}
```

完整 source gate：

```powershell
dotnet test .\AteraSnipeSync.sln --configuration Release --no-build --no-restore
git diff --check
```

当前 suite 是原有 317 tests 加 8 个 Installer contract tests，所以完整预期为 325/325。

## 4. Release build

开发阶段允许 dirty tree：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1 -AllowDirty
```

正式 artifact 必须从 clean commit 运行，不能传 `-AllowDirty`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1
```

项目所有者已于 2026-07-23 明确同意适用的 WiX 7 OSMF EULA，installer project 以 `AcceptEula=wix7` 记录该授权。若该值缺失或错误，`WIX7015` 应当使 release 失败。

成功输出：

```text
artifacts\release\v1.0.0\AteraSnipeSync-1.0.0-win-x64.msi
artifacts\release\v1.0.0\AteraSnipeSync-1.0.0-win-x64.msi.sha256
artifacts\release\v1.0.0\release-manifest.json
```

## 5. MSI 静态与 administrative extraction 验收

`Build-Release.ps1` 已自动执行一次 administrative extraction 和下列静态检查。也可在 disposable VM 上手工复核；administrative extraction 不得注册 service：

```powershell
$msi = '<absolute-path-to-msi>'
$extractRoot = Join-Path $env:TEMP 'AteraSnipeSync-admin-extract'
msiexec.exe /a $msi TARGETDIR=$extractRoot /qn /norestart /L*v "$extractRoot.log"
```

验证：

- 两个 EXE 位于同一个 `AteraSnipeSync` directory。
- 两个 EXE 的 ProductVersion 为 `1.0.0`、FileVersion 为 `1.0.0.0`、CompanyName 为 `Vue IT Inc.`。
- Tray EXE 和 MSI ARP icon 可读取。
- extracted tree 不含 `*.pdb`、`appsettings.Development.json`、`appsettings.local.json`、`*.local.json`、test assembly 或真实 credential。
- `.sha256` 与 `Get-FileHash -Algorithm SHA256` 一致。
- manifest 的 version/RID/commit/dirty/hash/manufacturer/productCode 正确；正式 clean build 的 `dirty` 必须为 `false`。

## 6. VM 安装与卸载矩阵

在 Windows 11 x64 与 Windows Server 2022 x64 分别执行以下项目。安装后在 `%ProgramData%\AteraSnipeSync` 建立无敏感信息 marker，例如 `installer-test-marker.txt`，并在每个 case 前恢复干净 snapshot。

1. 交互式卸载，不勾选 checkbox：MSI 和 service 删除，ProgramData root 与 marker 保留。
2. 交互式卸载，勾选 checkbox：整个 ProgramData root 消失。
3. 在 dialog 点击 Cancel：installed files、service、ARP entry 和 ProgramData marker 均不变化。
4. `msiexec /x <msi> /qn /norestart`：ProgramData 与 marker 保留。
5. `msiexec /x <msi> /qn /norestart REMOVELOCALDATA=1`：整个 ProgramData root 消失。
6. 安装临时 `1.0.1` major-upgrade MSI，即使传 `REMOVELOCALDATA=1`：原 ProgramData marker 保留。
7. repair：不删除、不覆盖、不重置 ProgramData marker/config。
8. 保持 Tray 运行后卸载：Restart Manager 正常提示关闭或重启；不得强杀后声称 cleanup 成功。
9. 确认 service 为 LocalSystem/automatic/running，all-users Start Menu shortcut 与 HKLM Run value 存在。

验收期间不配置真实 API key，不发起 sync，不测试真实 SMTP/webhook。完成后删除 VM snapshot、临时 extraction 和 marker；不得把 VM 生成的 config/log 加入 repository。

## 7. 常见失败

- `WIX7015`：确认 project 中的 `AcceptEula=wix7` 仍来自 2026-07-23 的 owner 授权，且没有被移除或改坏；不得使用其他未授权值绕过。
- merge collision：两个 publish 的同名文件 hash 不一致；不能选择“后写覆盖”，必须统一 runtime/package source。
- package 出现 PDB/local config：检查 publish properties、Worker `CopyToPublishDirectory` 与 WiX `Files/Exclude`。
- upgrade 删除 data：检查 condition 是否仍精确包含 `REMOVELOCALDATA=1 AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`。
- dialog 默认勾选：检查 `REMOVELOCALDATA` 是否被赋默认值，checkbox 是否只使用 `CheckBoxValue="1"`。
