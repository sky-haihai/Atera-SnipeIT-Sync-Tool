# Tray App - 功能职责

## 1. 模块目标

TrayApp Module 提供本机用户界面，用于配置和触发同步相关操作。

当前已实现阶段只提供 Atera API key 本地配置入口。

后续阶段 TrayApp 还应提供：

- Snipe-IT API 配置入口
- schedule 配置入口
- 手动 sync 入口
- 手动 sync preflight CSV 查看/确认入口
- latest status/report 查看入口

## 2. 当前职责

- 显示一个 Windows Forms 设置窗口
- 提供 Atera API key 输入框
- Atera API key 输入框必须使用 password field，不以明文显示用户输入
- 保存 Atera API key 到本机 local config
- 启动时从本机 local config 读取已保存的 Atera API key，并回填到 password field
- 保存时保留 local config 中已有的 Snipe-IT、Sync、Notification 等其它配置
- 保存成功或失败时向用户显示简短状态

## 2.1 后续 Schedule UI 职责

Schedule UI 应参考备份任务 schedule GUI 的常见模式，提供清晰的 daily / weekly / monthly 配置。

应支持：

- Enable schedule toggle
- Daily / Weekly / Monthly frequency selector
- Run time picker，支持一个或多个时间
- Weekly weekday checklist
- Monthly day-of-month selector
- Monthly last-day-of-month option
- Time zone selector
- Prevent overlapping runs option
- Save / Cancel

Schedule UI 只负责编辑 local config，不直接执行 sync。

## 2.2 后续 Manual Sync UI 职责

Manual sync UI 必须把直接同步和预览分成两个按钮：

### `Sync Now`

1. 用户点击 `Sync Now`
2. TrayApp 通过未来 IPC 请求 WorkerService 执行真实 sync
3. 该请求不得启用 manual preflight CSV
4. 真实 sync 直接走共享 pipeline，并保存最终 result/report/status

### `Preview Changes`

1. 用户点击 `Preview Changes`
2. 系统执行 preflight lookup/mapping/planning
3. 系统生成临时 CSV 修改计划
4. TrayApp 提供打开 CSV 所在目录或查看 CSV 的入口
5. 用户确认后执行真实 sync；确认后不按第一次 CSV 快照执行，而是重新计算当前状态
6. 用户取消时停止流程，不执行真实写入

Manual sync preflight CSV 必须和最终 sync result/report 分开。

Scheduler 自动 sync 不走该人工 CSV 确认流程。

## 3. 本机保存位置

默认 local config 文件：

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

默认 sync history/report 目录：

```text
C:\ProgramData\AteraSnipeSync\History
```

默认 manual sync 日志目录：

```text
C:\ProgramData\AteraSnipeSync\Logs
```

Manual sync 日志必须按本机日期拆分为 `ManualSync_yyyyMMdd.log`。每次写入日志时都要确认目录和当天文件存在；如果当天文件被删除，下一条日志写入必须重新创建该文件。

保存结构：

```json
{
  "Atera": {
    "BaseUrl": "https://app.atera.com/api/v3",
    "ApiKey": "<local api key>"
  },
  "SnipeIt": {
    "BaseUrl": "https://snipe.example.com/api/v1",
    "ApiToken": "<local api token>",
    "DefaultStatusId": 2,
    "MacAddressCustomFieldDbColumnName": "_snipeit_mac_address_1",
    "NameMatchThreshold": 0.92,
    "CreateMissingCompanies": false,
    "CreateMissingModels": false
  },
  "Mapping": {
    "DefaultCompanyName": "Unknown Company",
    "DefaultManufacturerName": "Unknown Manufacturer",
    "DefaultModelName": "Unknown Model",
    "DefaultCategoryName": "Computer",
    "CompanyAliases": {
      "Atera company name": "Snipe-IT company name"
    }
  }
}
```

该文件不得提交到 git。仓库中只允许提交 `samples/configs/appsettings.local.example.json` 这类占位模板。

## 4. 不负责的事情

本阶段 TrayApp 不负责：

- 调用真实 Atera API 验证 key
- 调用真实 Snipe-IT API
- 在自动化测试中读取真实 API key
- 创建 Python 探针
- 实现完整托盘菜单
- 实现 Run Now / Pause / Resume
- 实现 WorkerService 通信
- 实现 schedule editor
- 实现 manual sync preflight CSV viewer/confirmation

真实 API key 验证只能手动运行，并且必须有单独运行说明；不能进入普通 `dotnet test`、build 或 CI。

## 5. 临时 no-service manual 验证窗口

当前 TrayApp 入口可以临时打开 `ManualSyncForm`，让项目 owner/operator 在不安装、不运行 WorkerService 的情况下验证 manual sync。

这个临时窗口负责：

- 从本地 password field 收集 Atera 与 Snipe-IT 凭据
- 收集一次 sync 所需的 mapping 默认值和 import 开关
- 通过现有 dry-run/preflight CSV 路径执行 `Preview Changes`
- 通过现有真实 manual sync 路径执行 `Sync Now`，并在执行前要求用户确认
- 提供 `Test Atera` 按钮，通过现有 Atera pull 路径做只读连接验证
- 提供 `Test Snipe-IT` 按钮，通过 Snipe-IT 只读 hardware lookup 做连接验证
- 提供 `Save Config` 按钮，保存手动面板里可复用的 Atera、Snipe-IT、mapping 和 import 选项配置
- 启动时从 local config 回填已保存的手动面板配置
- `Create Missing Companies` 对新配置默认勾选，operator 仍可手动取消
- 显示真实进度条和当前阶段细节，让长时间 preview 能看到 Atera pull、mapping、Snipe-IT planning/CSV 等正在发生的步骤
- 将 sanitized UI log 同步写入 `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`
- 每次 Preview 或 Sync 完成后，通过 `JsonFileSyncStatusStore` 保存 `SyncResult_*.json` report/status，并在 UI log 中显示保存路径
- 保持真实 key 使用为 manual-only，不进入自动化测试
- 只显示 sanitized summary，不显示 secrets 或 raw API payload

这不替代后续正式 TrayApp/WorkerService 分工。完整 tray menu、IPC channel、持久化 status viewer、preview-confirm workflow 仍属于后续模块工作。
