# Atera Snipe-IT Auto Sync

[简体中文](README.zh-CN.md) | [English](README.md)

Atera Snipe-IT Auto Sync 是一个面向 Windows 的设备资产同步工具。它从 Atera 拉取 managed agents，将设备、客户、厂商、型号和硬件身份信息转换为 Snipe-IT 资产记录，然后通过 Windows Service 定时同步，或由管理员在 Tray App 中手动 Preview / Sync。

当前版本：`1.0.1`<br>
发行方：[VUE IT Inc.](https://vueit.ca/)<br>
运行平台：Windows x64

> [!IMPORTANT]
> `Sync Now` 和已启用的定时任务会真实修改 Snipe-IT，包括 soft-delete 已从 Atera 消失的 `ATERA-` 资产。首次使用、修改 mapping 配置或升级后，请先运行 `Preview` 并检查全部 CSV 计划。

## 工作流程

```text
Atera agents
    ↓ 完整分页读取
字段重建与标准化
    ↓
Snipe-IT references / hardware snapshot
    ↓ 匹配、冲突检查、变更规划
Company / Category / Model / Asset create or update
    ↓
过期 ATERA- asset soft-delete
    ↓
History / Logs / Notifications
```

Tray App 只负责配置、状态显示和向 Worker 发出命令；真正的 API 读取和同步由后台 Windows Service `AteraSnipeItAutoSync` 执行。

## Key features

- 从官方 Atera API 分页读取 managed agents，并处理认证错误、限流、暂时性错误、异常分页和 malformed records。
- 为每台设备生成稳定的 `ATERA-{AteraAgentId}` Asset Tag。
- 将 Atera customer、manufacturer、model、device name、serial、MAC、device type 和审计 notes 映射到 Snipe-IT。
- 支持 Company 和 Manufacturer aliases，例如 `Dell Inc.=Dell`。
- 可选择自动创建缺失的 Companies 和 Models；缺失的目标 Asset Category 会按需要创建。
- 按 Asset Tag、MAC、可靠 serial 等强身份匹配资产；强身份均未命中时，才在 Company + Category + Model 相同的范围内使用高置信度名称匹配。
- 检测重复 source ID、Asset Tag、可靠 serial 和未忽略 MAC，阻止有冲突的记录覆盖错误资产。
- 支持 Snipe-IT MAC custom field 和 Fieldset，可同时完成 MAC 写入、Model Fieldset 绑定和 Model Category normalization。
- `Preview` 是真正的 dry-run：允许读取 Atera 和 Snipe-IT，但不发送任何 `POST`、`PATCH` 或 `DELETE`，并生成四份 CSV 变更计划。
- 支持手动 `Sync Now`、Daily / Weekly / Monthly 定时任务、多个每日运行时间、Windows time zone 和防重叠执行。
- Dashboard 显示 Worker / Service / Schedule 状态、下次运行时间、当前活动，以及最近一次真实同步的 Created / Updated / No change / Deleted 计数。
- 每次运行写入结构化本地 History，并支持 Email、Teams Workflow Adaptive Card 或 Generic JSON webhook 通知。
- 安装包是 self-contained Windows x64 MSI，目标机器不需要预装 .NET Runtime。

## Fail-closed 删除安全策略

自动删除被设计为 fail-closed，而不是“尽量继续删除”。

- 只有 Asset Tag 以 `ATERA-` 开头的 Snipe-IT Hardware Asset 才属于自动同步 namespace；其他人工资产永不自动删除。
- 只有在 Atera 分页 pull 成功完成、且 Snipe-IT hardware snapshot 完整后，系统才会计算 `MissingFromAtera`。
- 系统用完整 Asset Tag 比较当前 Atera inventory 与 Snipe-IT。只存在于 Snipe-IT、且属于 `ATERA-` namespace 的资产会成为 soft-delete 候选。
- 重复记录自身会被标记为 `SnipeImport.DuplicateBatchIdentity` 并单独阻塞；其他正常记录仍可继续创建或更新。
- 但是，只要规划阶段出现任何 source validation、reference、matching 或 duplicate-target failure，本次运行的全部 `MissingFromAtera` 删除计划都会被清空。
- 被阻塞记录的 Asset Tag 仍被视为当前 Atera inventory 的一部分，避免因为临时数据异常而误删它自己的 Snipe-IT 资产。
- `Preview` 只把通过当前 deletion gates 的候选写成 `Operation=Delete`、`ChangeReasons=MissingFromAtera`，不会调用 DELETE。
- 真实删除使用 Snipe-IT hardware soft-delete。单个 delete 失败会被记录，但不会阻止其他已经安全规划的 delete 候选。

这意味着：遇到重复或匹配异常时，正常资产可以继续同步，但本轮所有过期资产删除会被关闭。管理员修复冲突并重新 Preview / Sync 后，删除才会恢复。

## 当前管理的 Snipe-IT 数据

| 对象 | 当前行为 |
| --- | --- |
| Hardware Assets | 创建、更新、no-op 判断，以及对过期 `ATERA-` 资产执行 soft-delete |
| Companies | 精确名称/alias 复用；可配置是否自动创建 |
| Categories | 查找 Default Category，缺失时按 Asset category 创建 |
| Models | 查找、可配置自动创建；可更新现有 Model 的 Category 和 Fieldset |
| Manufacturers | 只查找和绑定，不自动创建；缺失时记录 warning，Model 可不绑定 manufacturer |
| Custom Fields / Fieldsets | 只查找；不会自动创建 MAC custom field 或 Fieldset |

## Known issues / limitations

- 仅提供 Windows x64 版本；当前正式验收目标是 Windows 11 x64 和 Windows Server 2022 x64。
- MSI 当前未进行代码签名。Windows 可能显示 Unknown publisher / SmartScreen 警告；安装前请从可信来源获取 MSI 并校验随包 SHA-256。
- Atera API key、Snipe-IT token 和 SMTP password 当前以明文保存在 `%ProgramData%\AteraSnipeSync\appsettings.local.json`。密码框只负责界面遮罩，尚未使用 DPAPI、Credential Manager 或 secret vault。请只在受信任的服务器上部署，不要复制、打印、记录或提交该文件。
- 当前 Atera source 只读取 agents endpoint；Company 关联使用 agent payload 中的 customer fields，不单独同步完整 Atera customer directory。
- Atera page 中个别 malformed 或缺少必要 identity 的 record 当前会被跳过并产生 warning，而不是让整个 pull 失败。如果该设备以前已经同步，它可能因此成为 `MissingFromAtera` 候选；发现 Atera malformed-record warning 时不要执行真实同步，应先检查 Preview 并修复 source data。
- 所有导入设备当前映射到一个 `Default category`，尚不支持按 device type 配置多套 Category mapping。
- Manufacturer 不会自动创建。必须预先在 Snipe-IT 建立 Manufacturer，或配置 alias；否则 Model 会在没有 manufacturer binding 的情况下处理，并产生 warning。
- `Normalize from categories` 会扫描并可能修改 Snipe-IT 中现有 Models，而不只修改本次 Atera agents 使用的 Models。配置错误可能扩大修改范围。
- `Ignored device types` 是从当前 inventory 中排除设备，而不是“保留但不更新”。以前已同步的同类 `ATERA-` 资产可能因此成为 `MissingFromAtera` 候选。
- 成功但为空的完整 Atera inventory 会把所有 `ATERA-` 资产视为过期。此行为有 snapshot/failure gates 保护，但仍必须先 Preview。
- 删除是 Snipe-IT soft-delete；本工具没有 restore、archive 或 permanent purge UI。
- `Sync Now` 当前确认框只提示 create/update，但实际运行还可能 soft-delete。以 Preview CSV 为准。
- Worker 使用单一全局 non-overlap gate。Preview、Sync Now、Connection Test 和 scheduled sync 不会并行；Worker busy 时新操作不会排队。
- 定时状态会持久化；停机期间多个 overdue occurrences 最多合并为一次补跑，不会逐次回放全部错过的时间点。
- Health Check / Snipe-IT duplicate-data repair 仍在 roadmap 中。当前同步会报告歧义并阻塞相关记录，但不会自动 merge、rename 或修复既有重复 references。
- History 默认保留 90 天且最多 500 份；Tray daily logs 默认保留 30 天。这些 retention 参数当前不在 Config GUI 中开放。
- Preview 目录目前没有自动 retention 清理；需要管理员按组织政策定期清理。

## 安装前准备

需要准备：

1. 一台 Windows x64 机器，并使用可执行 MSI 安装的本机管理员账号。
2. Atera API key，至少能够读取 agents。
3. Snipe-IT HTTPS 地址和 API token。Token 必须能够读取本工具需要的 Hardware、Companies、Categories、Models、Manufacturers、Fieldsets；真实同步还需要对所启用对象执行 create/update，以及对过期 Hardware 执行 delete。
4. Snipe-IT 中一个有效的 Asset Status ID。
5. 可选：一个用于保存 MAC 的 Snipe-IT custom field，以及包含该字段的 Fieldset。
6. 可选：SMTP relay、Teams Workflow URL 或其他 HTTPS webhook。

建议先备份 Snipe-IT，并在测试环境完成至少一次 Preview 和一次小范围真实同步。

## 安装

### 1. 校验安装包

发行目录包含：

```text
AteraSnipeSync-1.0.0-win-x64.msi
AteraSnipeSync-1.0.0-win-x64.msi.sha256
release-manifest.json
```

在 PowerShell 中计算 hash，并与 `.sha256` 或 `release-manifest.json` 比较：

```powershell
Get-FileHash -LiteralPath .\AteraSnipeSync-1.0.0-win-x64.msi -Algorithm SHA256
Get-Content -LiteralPath .\AteraSnipeSync-1.0.0-win-x64.msi.sha256
```

### 2. 安装 MSI

从管理员 PowerShell 或 Windows Terminal 运行：

```powershell
msiexec.exe /i .\AteraSnipeSync-1.0.0-win-x64.msi /norestart
```

安装完成后：

- 程序安装到 `C:\Program Files\AteraSnipeSync`。
- Windows Service `AteraSnipeItAutoSync` 以 LocalSystem、Automatic 方式安装并启动。
- 所有用户的 Start Menu 中出现 `Atera Snipe-IT Auto Sync`。
- Tray App 注册为用户登录后自动启动。
- 本地数据目录建立在 `C:\ProgramData\AteraSnipeSync`。

如果 Tray icon 没有立即出现，可从 Start Menu 手动打开 `Atera Snipe-IT Auto Sync`。退出 Tray App 不会停止后台 Worker Service。

## 首次配置与使用

推荐的首次上线顺序：

1. 打开 Tray icon 或 Start Menu 中的 Dashboard。
2. 点击 `Configuration`，完成下面的四个页签。
3. 在 `API & Credentials` 点击 `Test Connections`，确认 Atera 和 Snipe-IT 都显示 Success。
4. 保存配置，但先不要启用 Schedule。
5. 回到 Dashboard，点击 `Preview`。
6. 打开 `C:\ProgramData\AteraSnipeSync\Preflight\<run-id>`，检查四份 CSV。
7. 处理全部 `Blocked`、重复身份、歧义匹配和意外的 Add / Modify / Delete。
8. 确认每个自动管理的 Asset Tag 都是预期的 `ATERA-...`，并重点检查所有 `MissingFromAtera` 行。
9. 回到 Dashboard，点击 `Sync Now` 并确认执行。
10. 检查 Dashboard 计数、Logs、History 和 Snipe-IT 结果。
11. 结果稳定后，再启用 Schedule 和 Notifications。

### Preview 输出

每次 Preview 会建立一个新的 run directory，并写入：

| 文件 | 内容 |
| --- | --- |
| `snipeit-assets-plan.csv` | Asset 的 Add / Modify / Blocked / Delete、匹配 id、冲突证据和变更原因 |
| `snipeit-companies-plan.csv` | 缺失 Company 的创建计划 |
| `snipeit-categories-plan.csv` | 缺失 Asset Category 的创建计划 |
| `snipeit-models-plan.csv` | Model 创建、Category normalization 和 Fieldset 修改计划 |

CSV 中外部来源值会做 spreadsheet formula neutralization，但文件仍可能包含客户和设备信息，应按敏感运维数据处理。

### Dashboard 操作

| 操作 | 作用 |
| --- | --- |
| `Preview` | 执行完整读取和规划、写 CSV，但不修改 Snipe-IT |
| `Sync Now` | 执行一次真实同步，可能 create、update 和 soft-delete |
| `Cancel` | 请求取消当前 Worker 操作；已经成功的写入不会回滚 |
| `Restart Service` | 通过 UAC 提权重启/修复 Worker Service 状态 |
| `Open Log Folder` | 打开受控的本地运行数据目录 |
| `Configuration` | 编辑并保存共享的 Tray / Worker 配置 |

## Config GUI 详细教程

### API & Credentials

| 字段 | 是否必填 | 配置方法 |
| --- | --- | --- |
| `Atera API base URL` | 是 | 保持 `https://app.atera.com/api/v3/`。安全校验只接受官方 `app.atera.com` HTTPS host。 |
| `Atera API key` | 是 | 粘贴 Atera API key。字段在界面中遮罩，但保存后仍是本机 JSON 明文。 |
| `Snipe-IT API base URL` | 是 | 输入完整 API v1 根地址，例如 `https://assets.example.com/api/v1`。非 loopback 地址必须使用 HTTPS，并且路径必须以 `/api/v1` 结尾。 |
| `Snipe-IT API token` | 是 | 粘贴 Snipe-IT Bearer token。使用具备所需读写权限、且权限尽量小的 service account token。 |

点击 `Test Connections` 时，GUI 会先验证并保存当前整份配置，再要求 Worker reload，然后执行只读的 bounded probes：Atera 读取最多一页，Snipe-IT 请求一个 hardware row。这个测试能验证基本 URL、认证和读取权限，但不能证明真实 create/update/delete 权限，也不会验证所有 reference endpoints。

### Mapping & Import

#### Company aliases

每行一个 `source=target`，例如：

```text
Vue IT Incorporated=Vue IT Inc.
Contoso Canada Ltd.=Contoso
```

规则：

- 每行必须且只能包含一个 `=`，不能使用 `=>`。
- key 和 value 都不能留空。
- 匹配不区分大小写，并会统一连续空白和 dash-like 字符。
- 系统先查找原始 Atera company name；只有原名称在 Snipe-IT 不存在时才尝试 alias，避免把已经存在的精确名称错误重定向。
- Atera 缺少 customer name 时，固定 fallback 是 `Unknown Company`。

#### Manufacturer aliases

格式同 Company aliases，例如：

```text
Dell Inc.=Dell
LENOVO=Lenovo
```

系统先查 source manufacturer，再查 alias。目标 Manufacturer 仍不存在时不会自动创建，而是产生 warning，并允许 Model 不绑定 manufacturer。

#### 其他 Mapping / Import 字段

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Default category` | `Computer` | 所有当前导入设备的目标 Asset Category，也是 Model category normalization 的目标。该 Category 不存在时会创建。 |
| `Normalize from categories (;)` | `Server; Laptop; Desktop` | 以分号、逗号或换行分隔。现有 Snipe-IT Models 如果位于这些 category，会被规划移动到 `Default category`。至少需要一个值。若不希望跨 Category normalization，可将这里设为与 `Default category` 相同的单一值，例如 `Computer`。 |
| `Ignored device types (;)` | 空 | 完全排除这些 Atera device types。名称 trim 后按不区分大小写精确匹配。注意：已存在的同类 `ATERA-` 资产可能成为删除候选。 |
| `Default status ID` | `2` | 正整数。创建和更新 Hardware 时写入这个 Snipe-IT Status ID；现有资产不同会产生 `Status` 修改。请在 Snipe-IT Status Labels 中确认实际 ID，不要只按名称猜测。 |
| `MAC custom field DB column` | 空 | Snipe-IT custom field 的数据库列名，通常类似 `_snipeit_mac_address_5`。必须复制真实 DB column，不是显示名称。 |
| `MAC fieldset name` | 空 | 包含上述 MAC field 的 Snipe-IT Fieldset 精确名称。它与 DB column 必须同时填写或同时留空。 |
| `Ignored MAC addresses (;)` | 空 | 忽略常见虚拟、共享或无意义 MAC。接受冒号、连字符、点号或无分隔的 12 位十六进制格式；保存时统一为 `AA:BB:CC:DD:EE:FF`。 |
| `Name match threshold` | `0.92` | 范围 `(0, 1]`。仅在 Asset Tag / MAC / reliable serial 都没有命中后使用；值越高越保守。名称 fallback 还要求 Company、Category、Model context 相同。 |
| `Create missing companies` | 开 | 开启时创建缺失 Company；关闭时相关资产被 Blocked。 |
| `Create missing models` | 关 | 开启时创建缺失 Model；关闭时相关资产被 Blocked。建议首次部署保持关闭，先通过 Preview 整理 Models 后再决定。 |

MAC 配置行为：

- 两个 MAC 字段都留空时，MAC matching 和 MAC payload 写入关闭，系统产生一次提示 warning。
- 启用时，Fieldset 必须存在，并且必须包含指定 custom field；否则相关 planning 会失败。
- 一个设备有多个 MAC 时，当前 payload 写入第一个合法且未忽略的 MAC；全部合法 MAC 仍保留在 Preview 审计列中。
- ignored MAC 不参与重复检测、existing asset matching 或 payload 选择。
- 配置 Fieldset 后，系统还会把已经在目标 Category、或本轮被 normalization 到目标 Category 的 Models 绑定到这个 Fieldset。请检查 `snipeit-models-plan.csv`。

重复判断说明：

- 重复 source ID、Asset Tag、可靠 serial 或未忽略 MAC 会阻塞冲突记录。
- 已知 placeholder serial 会被视为不可靠身份。
- 多台 `Virtual Machine` 共享 serial 时，该 serial 仅用于审计，不参与匹配或 payload，避免常见虚拟机 serial 误阻塞。

### Schedule

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Frequency` | `Daily` | 可选 `Daily`、`Weekly`、`Monthly`。 |
| `Schedule enabled` | 关 | 开启后 Worker 按下面规则执行真实同步，不是 Preview。 |
| `Windows time zone ID` | 当前 Windows 时区 | 必须是运行机器可识别的 Windows time zone ID，例如 `Mountain Standard Time`。 |
| `Run times (;, HH:mm)` | `02:00` | 24 小时 `HH:mm` 格式；可用分号、逗号或换行配置多个唯一时间，例如 `02:00; 14:00`。 |
| `Weekly days (;)` | `Monday` | 仅 Weekly 显示。使用英文 `DayOfWeek` 名称，例如 `Monday; Wednesday; Friday`。 |
| `Monthly days (;)` | `1` | 仅 Monthly 显示。允许 `1` 到 `31`；不存在该日期的月份不会按该日运行。 |
| `Also run on the last day of month` | 关 | 仅 Monthly 显示。可单独使用，也可与指定日期一起使用。 |

所有 Worker 操作都禁止重叠。如果 scheduled time 到达时另一个 Preview、Sync、Connection Test 或 scheduled run 正在执行，该次 scheduled operation 不会并行或排队。

点击 `Save changes` 后，Dashboard 会要求 Worker reload schedule。确认 Dashboard 显示 `Schedule enabled` 和正确的 `Next run`；仅写入 JSON 不等于 Worker 已成功应用配置。

### Notifications

#### 总开关和事件

| 字段 | 说明 |
| --- | --- |
| `Notifications enabled` | 总开关。关闭时不会发送普通运行通知。 |
| `Sync completed` | 为 scheduled sync、manual Sync Now 和 manual Preview 的 completed events 发送通知。Completed run 仍可能包含 record-level warnings/failures，应查看计数和 History。 |
| `Sync failed` | 为未完成或 fatal 的 scheduled/manual/preview events 发送通知。 |

至少要开启总开关、选择一个事件，并完整配置一个 channel，普通运行通知才会发送。

#### Email / SMTP

| 字段 | 配置方法 |
| --- | --- |
| `SMTP host` | SMTP server hostname；配置 Email 时必填。 |
| `SMTP port` | `1` 到 `65535`，默认 `587`。 |
| `Use TLS/SSL for SMTP` | 默认开启，映射到 SMTP client 的 SSL/TLS 选项。 |
| `SMTP username` / `SMTP password` | 必须同时填写或同时留空。都留空时使用匿名/IP-based relay，不会使用 LocalSystem 的 Windows credentials。 |
| `Email from` | 有效的单一发件地址；配置 Email 时必填。 |
| `Email to` | 一个或多个有效收件地址；可用分号、逗号或换行分隔。 |

#### Webhook

| 字段 | 配置方法 |
| --- | --- |
| `Webhook format` | `Teams Workflow (Adaptive Card)` 用于接受 message + Adaptive Card envelope 的 Teams Workflow；`Generic JSON` 用于普通 HTTPS endpoint。 |
| `Webhook URL` | 必须是绝对 HTTPS URL。URL 被视为 secret，不应出现在工单、日志或截图中。 |

Generic JSON 的顶层字段为 `eventType`、`severity`、`subject`、`message`、数字类型的 `deleted` 和 `occurredAtUtc`。

点击 `Test Notifications` 会先保存当前配置，然后向每个完整配置的 Email / Webhook channel 发送一条真实测试消息。`Accepted` 只代表 SMTP/webhook endpoint 接受了请求，不保证最终投递或下游工作流处理成功。测试发送不依赖普通通知总开关和事件选择。

### Save changes 与 Cancel

- `Save changes` 会验证整份配置并原子写入共享 JSON。任意必填项、URL、MAC、schedule、alias、SMTP 或 webhook 格式错误都会阻止保存。
- `Cancel` 返回 Dashboard，不保存当前页面改动，也不会 reload Worker 或调用外部 API。
- 测试按钮也会保存当前整份配置；不要把它们当成“不保存的临时测试”。

## 本地文件与保留策略

| 路径 | 内容 | 默认保留 |
| --- | --- | --- |
| `C:\ProgramData\AteraSnipeSync\appsettings.local.json` | 完整共享配置和明文 credentials | 直到管理员删除或选择卸载时清理 |
| `C:\ProgramData\AteraSnipeSync\schedule-state.json` | 下次/上次 scheduled occurrence 和规则 fingerprint，不含 secret | 直到管理员删除或卸载清理 |
| `C:\ProgramData\AteraSnipeSync\Logs` | Tray daily operation logs | 30 天 |
| `C:\ProgramData\AteraSnipeSync\History` | 每次 completed run 的结构化 JSON | 90 天且最多 500 份 |
| `C:\ProgramData\AteraSnipeSync\Preflight\<run-id>` | Preview CSV | 当前无自动清理 |

Logs、History 和 IPC summary 会避免写入 API tokens、raw HTTP payloads 和完整 Atera inventory，但仍包含用于排查的有限资产身份与客户信息，应按组织的数据保护要求管理。

## 卸载

### 交互式卸载

从 Windows Installed apps / Programs and Features 卸载 `Atera Snipe-IT Auto Sync`，或运行：

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /norestart
```

卸载对话框会询问是否删除：

```text
%ProgramData%\AteraSnipeSync
```

默认不勾选，因此配置、credentials、logs、history、preview files 和 schedule state 会保留，便于以后重装。勾选删除后整个本地数据目录会被删除且不可恢复。

### 静默卸载

保留所有本地数据：

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart
```

同时永久删除整个本地数据目录：

```powershell
msiexec.exe /x .\AteraSnipeSync-1.0.0-win-x64.msi /qn /norestart REMOVELOCALDATA=1
```

`REMOVELOCALDATA=1` 只在真正 uninstall 时生效。Major upgrade 和 repair 即使收到该参数也不会删除本地数据。

> [!CAUTION]
> 删除 `%ProgramData%\AteraSnipeSync` 会删除明文 credentials、所有日志、同步历史、Preview CSV 和调度状态。操作前按组织政策备份需要保留的审计记录。

## 常见问题排查

### Service 显示 Running，但 Worker Offline

1. 在 Dashboard 点击 `Restart Service` 并接受 UAC。
2. 检查 `C:\ProgramData\AteraSnipeSync\Logs`。
3. 确认配置文件是合法 JSON，且 LocalSystem 能读取 ProgramData 目录。
4. 确认安装目录中的 Worker 和 Tray 版本一致。

### 配置保存成功，但 Schedule 无效

- 查看 Dashboard 的 Schedule status 和 error，而不是只看保存提示。
- 确认 Windows time zone ID 存在、Run times 使用 `HH:mm`，Weekly 有 days，Monthly 有 days 或 last-day 选项。
- 再次保存，确认 Worker reload 成功；必要时重启 Service。

### Preview 出现很多 Blocked

- 查看 `FailureCode`、`ConflictingFields`、`ConflictingValue` 和 `ConflictingAssets`。
- 修复 Atera 中重复 Agent ID / serial / MAC，或把已知共享 MAC 加入 `Ignored MAC addresses`。
- 清理 Snipe-IT 中重复 Asset Tag、serial、model/reference records；当前工具不会自动选择或合并歧义记录。
- 如果 Company / Model missing，检查 alias 和 `Create missing ...` 开关。

### Preview 没有任何 Delete

如果本轮存在任意 validation、reference、matching 或 duplicate-target failure，fail-closed gate 会清空全部删除计划。先修复所有 Blocked/failure，再重新 Preview。

## 开发与验证

需要 .NET 10 SDK。还原、构建和运行离线自动化测试：

```powershell
dotnet restore .\AteraSnipeSync.sln
dotnet build .\AteraSnipeSync.sln --no-restore
dotnet test .\AteraSnipeSync.sln --no-build --no-restore
```

自动化测试使用 mocked HTTP handlers、fake clients 和本地 fixtures，不会调用真实 Atera 或 Snipe-IT API。

开发阶段允许从 dirty worktree 构建 release：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1 -AllowDirty
```

正式 artifact 必须从 clean commit 构建，不使用 `-AllowDirty`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-Release.ps1
```

主要项目：

- `src/AteraSnipeSync.Core`：Atera pull、mapping、Snipe-IT import、scheduling、status 和 notification 核心逻辑。
- `src/AteraSnipeSync.WorkerService`：Windows Service、scheduler、runtime composition 和本机 IPC。
- `src/AteraSnipeSync.TrayApp`：Dashboard、Configuration GUI 和 service maintenance UI。
- `tests/AteraSnipeSync.Tests`：离线 unit / contract / integration-style tests。
- `installer/AteraSnipeSync.Installer`：WiX 7 MSI installer。
- `docs`：模块职责、技术规格、测试指南、流程图和 roadmap。

不要把真实 API key、token、SMTP password、webhook URL 或生产 `appsettings.local.json` 加入 repository。
