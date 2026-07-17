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

失败的 Preview/Sync 必须另外写入同目录的 `ManualSync_Error_yyyyMMdd.log`。该文件只记录失败 run 的时间范围、模式、汇总、完整 grouped failure reasons 和对应 history report 路径，不记录正常 progress；错误理由不得受 UI 最多显示 10 条的限制。error log 与普通日志使用相同的脱敏和 30 天保留规则。

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
    "ModelCategoriesToNormalize": ["Server", "Laptop", "Desktop"],
    "MacAddressCustomFieldDbColumnName": "_snipeit_mac_address_1",
    "IgnoredMacAddresses": ["00:09:0F:AA:00:01", "00:09:0F:FE:00:01"],
    "NameMatchThreshold": 0.92,
    "CreateMissingCompanies": false,
    "CreateMissingModels": false
  },
  "Mapping": {
    "DefaultCompanyName": "Unknown Company",
    "DefaultManufacturerName": "Unknown Manufacturer",
    "DefaultModelName": "Unknown Model",
    "DefaultCategoryName": "Computer",
    "IgnoredDeviceTypes": ["Server", "SNMP", "TCP"],
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
- 提供 common Atera device type 勾选列表，并把选中的值保存为 `Mapping.IgnoredDeviceTypes`
- 提供 `Ignore MAC Addresses` 文本框，以 `;` 分隔多个值；加载时从 `SnipeIt.IgnoredMacAddresses` 数组回填，保存时规范化、去重并写回该数组
- 通过现有 dry-run/preflight CSV 路径执行 `Preview Changes`
- 通过现有真实 manual sync 路径执行 `Sync Now`，并在执行前要求用户确认
- 提供 `Test Atera` 按钮，通过现有 Atera pull 路径做只读连接验证
- 提供 `Test Snipe-IT` 按钮，通过 Snipe-IT 只读 hardware lookup 做连接验证
- 提供 `Save Config` 按钮，保存手动面板里可复用的 Atera、Snipe-IT、mapping 和 import 选项配置
- 启动时从 local config 回填已保存的手动面板配置
- `Create Missing Companies` 对新配置默认勾选，operator 仍可手动取消
- 显示真实进度条和当前阶段细节，让长时间 preview 能看到 Atera pull、mapping、Snipe-IT planning/CSV 等正在发生的步骤
- 将完整、sanitized 的 progress detail 逐条写入 `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`；文件日志与 UI log 使用不同展示策略
- 将失败 Preview/Sync 的完整 sanitized failure groups 写入 `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_Error_yyyyMMdd.log`，成功 run 不写 error log
- 每次 Preview 或 Sync 完成后，通过 `JsonFileSyncStatusStore` 保存 `SyncResult_*.json` report/status，并将保存路径写入文件日志
- 完成结果中的 Snipe Import failure 必须按原始 failure code/root message 合并记录发生次数，并保留少量受影响 target 示例；合并后的 reason 必须按发生次数降序写入文件日志与 report，使影响大量 asset 的共享 reference 根因优先于零散单笔失败；其它 stage failure 按 stage/code/message 合并并使用相同优先级
- 保持真实 key 使用为 manual-only，不进入自动化测试
- Manual Preview/Sync 的 UI log 只显示 `Starting sync.`、`Processing models.`、`Processing categories.`、`Processing assets.`、`Completed.` 五个阶段；失败或取消可显示简短状态，但不得显示 agent 名称、asset 名称、计数、路径或逐笔细节
- 不显示 secrets 或 raw API payload

这不替代后续正式 TrayApp/WorkerService 分工。完整 tray menu、IPC channel、持久化 status viewer、preview-confirm workflow 仍属于后续模块工作。

## 13. 2026-07 UI、配置与日志加固职责

- Manual Sync 功能测试阶段，`Save Config` 必须把 Atera API key 与 Snipe-IT API token 保存到 `C:\ProgramData\AteraSnipeSync\appsettings.local.json`，TrayApp 每次启动时从该文件读取并回填，避免 operator 重复输入。
- 该本地文件在当前测试阶段包含明文凭据，必须视为敏感文件、不得提交到 git，也不得在 UI 日志或自动化测试输出中打印凭据。该临时策略只适用于 Manual Sync；WorkerService 的凭据读取与安全策略不在本次调整范围内。
- 本地配置保存必须复用原子、跨进程串行化的 settings store，避免 TrayApp 与 WorkerService 并发覆盖。
- Manual Sync、Preview Changes 与 WorkerService 必须读取同一个 `SnipeIt.IgnoredMacAddresses` 配置，不能只在 UI 预览阶段临时过滤。
- URL 校验必须拒绝非 HTTPS（仅测试/开发 loopback 可显式例外）；Snipe base URL 必须包含 `/api/v1`。
- Atera connection test 只执行一页一条的最小探测。
- 详细进度通过不丢 entry 的异步 writer 写入每日文件，不能在 UI thread 为每个 progress event 同步磁盘写入；正常运行期间不得因 queue capacity 或 UI 节流丢弃 progress，日志按默认 30 天清理。
- run 得到结果后，report 保存使用独立于 UI cancellation 的 token，确保真实写入后的部分取消仍可审计。

## 14. Model Category 归一化 UI 职责

- Manual Sync window 新增独立的 `Normalize Model Categories` 按钮，不把该维护动作混入 Preview 或 Sync Now。
- 点击后使用当前 Snipe-IT URL/token、`Default Category` 文本框和可编辑的 `Normalize From Categories (;)` 文本框；来源列表默认 `Server; Laptop; Desktop`，以分号分隔。
- 先执行只读 scan/plan，在 UI log 显示总 model 数、来源列表与 candidate model 数；只处理来源列表命中的 categories，未命中的其它 inventory categories 不得进入 plan。任何 planning 错误都不得产生 PUT。
- candidate 非空时弹出确认框，明确说明 Category 属于 Model、一次 model 更新会影响所有引用它的 assets，并列出本次目标 category 与数量。用户拒绝时记录取消且不写入。
- 确认后执行逐 model 更新，复用现有 progress/cancel/running-state 控制；运行期间其它按钮不可用。
- 每次点击创建独立的 `ModelCategoryNormalization_yyyyMMdd_HHmmss_fffffff.log`，记录开始/结束、扫描汇总、每个 candidate 的 model id/name、旧/新 category、成功或安全失败 code/message，以及最终 updated/failed/cancelled 汇总。
- UI 和专用日志不得记录 token、Authorization header、raw response body 或 raw payload。即使 scan/execute 抛异常或用户拒绝确认，也要尽力完成该次日志并在 UI 显示绝对路径。

## 15. Manual Sync UI 与文件日志分离职责

- `ManualSync_yyyyMMdd.log` 必须逐条保留 Core 发出的全部 sanitized progress，包括每个 agent/asset、reference creating/created/failed、snapshot、planning、CSV 和 completion entry；不得按时间、每 N 项 milestone 或 UI 阶段进行抽样。
- Manual Preview/Sync 的 UI log 不显示逐笔 progress、agent/asset 名称、`(Current/Total)`、summary count、report 路径或 failure detail，只按顺序显示 `Starting sync.`、`Processing models.`、`Processing categories.`、`Processing assets.`、`Completed.`。
- model/category/asset 阶段在一次 run 中各只显示一次；提前出现的 Atera agent 或 mapping asset detail 不得触发 UI asset 阶段。
- 失败或取消可以用一条简短 UI 状态替代 `Completed.`，完整原因仍写普通文件日志、专用 error log 与 history report。
- 文件写入保持异步且按 30 天保留；writer 必须在关闭时 flush 已接受 entry，不能使用 `DropWrite` 或其它正常容量溢出丢弃策略。

## 16. 单一 Category 配置与 Preview 可见性职责

- Manual Sync window 必须提供一个 `Default Category` 输入，默认 `Computer`；Server 与其它 DeviceType 共用该值。
- Load/Save Config、Preview Changes、Sync Now 与 Worker 共享同一个 `Mapping.DefaultCategoryName`，不允许 UI-only 临时值。
- `Normalize Model Categories` 以该字段作为目标，并只处理 `SnipeIt.ModelCategoriesToNormalize` 指定的来源 categories。
- Preview 结果必须把 Model category/name-number 冲突显示为失败，并在 `snipeit-assets-plan.csv` 的最后一列显示 `DeviceType`，用于审计 source 类型；该列不表示 category routing。
- 读取短期双字段配置时 UI 使用 `DefaultComputerCategoryName` 作为单一值 fallback；下一次保存只写 `DefaultCategoryName` 并移除两个双字段。

## 17. MAC Fieldset Name 配置与统一 Model 准备职责

- Manual Sync window 新增 `MAC Fieldset Name` 单行文本框，由 operator 填写当前 Snipe-IT 实例中的真实 Fieldset 名称；不得假设 `Assets with MAC Address` 是官方默认名称。
- Load/Save Config 使用 `SnipeIt.MacAddressFieldsetName` 持久化该值。
- 当填写了 `MAC Custom Field DB Column` 时，`MAC Fieldset Name` 必须同时非空；反之亦然。Preview/Sync request 构造阶段先验证配对，避免运行到 asset writes 才失败。
- `BuildBaseRequest` 必须把 Fieldset name、`DefaultCategoryName` 和 `ModelCategoriesToNormalize` 一并传给 `SnipeImportOptions`，因此 Preview Changes、Sync Now 与 Worker 使用同一 Model category/fieldset planning。
- `Normalize Categories` 独立按钮不再承担唯一修复入口；普通 Preview/Sync 自动显示并执行同样的 category Modify。按钮可从 UI 移除，避免 operator 误以为必须先执行第二套维护流程。
- Preview summary 必须显示 planned Model creates 与 planned Model updates；真实 Sync summary 必须显示 created/updated Model 数。

## 18. Manufacturer Alias 配置职责

- Manual Sync window 必须提供 `Manufacturer Aliases` 多行文本框，格式为每行 `Atera manufacturer=Snipe-IT manufacturer`；允许空行和 `#` 注释。
- Load/Save Config 使用 `Mapping.ManufacturerAliases` 持久化该字典，并保留 operator 配置的 canonical target 名称。
- Preview Changes、Sync Now 与 Worker 必须通过 `MappingOptions.ManufacturerAliases` 使用同一 alias 表。
- 格式错误、缺少任一侧名称、使用多个 `=` 或旧 `=>` 语法时，UI 必须在启动运行前拒绝并显示可操作错误。
- UI 不提供 fuzzy similarity 阈值作为 manufacturer 自动匹配开关。

## 19. Manual Sync 真实工作量进度职责

- Manual Sync progress bar 不得把任意子任务自己的 `Current/Total` 当作整次 run 的百分比。hardware/model snapshot 即使完成自身分页，也不能让整次 run 跳到 95%。
- 真实 Sync 使用阶段加权：Atera pull + mapping 为 0–5%，batch validation 为 5–10%，model/category/reference planning 与 snapshots 为 10–15%，逐 asset matching/planning 为 15–30%，reference writes 为 30–35%，逐 asset create/update 为 35–99%，只有整个 orchestrator 完成后显示 100%。
- Preview 没有真实 asset writes，因此逐 asset matching/planning 使用 15–95%，CSV/dry-run finalize 使用 95–99%，整个 orchestrator 完成后显示 100%。
- 进度在一次 run 内只能单调前进。子阶段消息乱序、重复 starting/completed callback 或 blocked/failed asset callback 不得让百分比倒退。
- 逐 asset create/update 的 `Current/Total` 必须在 35–99% 内线性映射。以 485 个 asset 为例，开始执行约 35%，执行 243 个约 67%，执行 485 个约 99%。
- 失败 callback 可以直接结束到 100%，但普通 `Snipe-IT import stage completed` 只能到 99%，最终 `Sync run completed` 才到 100%。
