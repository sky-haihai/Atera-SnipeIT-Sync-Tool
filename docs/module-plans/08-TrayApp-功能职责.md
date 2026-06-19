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

保存结构：

```json
{
  "Atera": {
    "ApiKey": "<local api key>"
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
