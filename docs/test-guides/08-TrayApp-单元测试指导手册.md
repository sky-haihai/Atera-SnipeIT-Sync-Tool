# Tray App - 单元测试指导手册

## 1. 测试范围

Current automated tests cover local configuration persistence used by TrayApp and WorkerService. They do not call real Atera or Snipe-IT APIs and do not write to `C:\ProgramData`.

Covered behavior:

- `LocalAppSettingsStore` returns `null` when the local config file does not exist
- saving Atera API key creates local config, trims the value, and allows a new store instance to reload it
- saving preserves unrelated JSON sections such as `SnipeIt`, `Sync`, and `Notifications`
- saving manual sync settings persists reusable Atera/Snipe-IT credentials, mapping, alias, and import option values
- loading manual sync settings returns credentials and other values saved in `Atera`, `SnipeIt`, and `Mapping`
- blank API key fails validation
- blank required manual sync secrets fail validation before save
- malformed local JSON fails with `InvalidOperationException`
- schedule config can be saved and loaded from `Sync.Schedule`
- invalid weekly/monthly schedule config is rejected before save
- default manual preflight path uses `C:\ProgramData\AteraSnipeSync\Preflight\{runId}`
- default manual log path uses `C:\ProgramData\AteraSnipeSync\Logs`
- manual `Sync Now` request disables preflight CSV and runs as real manual sync
- manual `Preview Changes` request enables dry-run plus preflight CSV
- manual progress UI displays a determinate progress bar and current stage/detail text from safe progress callbacks
- daily manual log writer flushes every accepted entry, including volumes above the previous bounded queue capacity
- manual Preview/Sync UI milestone tracker suppresses agent/asset detail and emits only ordered model/category/asset stages plus start/completion
- manual weighted progress keeps fast model/category preparation at or below 15% and assigns the majority of real Sync progress to per-asset execution

Current automated tests do not cover:

- WinForms visual password masking
- actual write to machine-level `C:\ProgramData\AteraSnipeSync\appsettings.local.json`
- real API key validation
- future manual sync confirmation UI
- visual verification that Snipe Import failures are grouped by original code/root message with an occurrence count and affected-target examples
- visual/filesystem verification of the machine-level dedicated `ManualSync_Error_yyyyMMdd.log`

## 2. 测试文件

```text
tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs
tests/AteraSnipeSync.Tests/TrayApp/ManualSyncLoggingTests.cs
tests/AteraSnipeSync.Tests/TrayApp/ManualSyncProgressCalculatorTests.cs
```

Related production code:

```text
src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs
src/AteraSnipeSync.TrayApp/ManualSyncForm.cs
src/AteraSnipeSync.TrayApp/DailyLogWriter.cs
src/AteraSnipeSync.TrayApp/ManualSyncUiStageTracker.cs
src/AteraSnipeSync.TrayApp/ManualSyncProgressCalculator.cs
src/AteraSnipeSync.TrayApp/Program.cs
src/AteraSnipeSync.TrayApp/SettingsForm.cs
src/AteraSnipeSync.TrayApp/SettingsForm.Designer.cs
```

## 3. 测试命令

Run from repository root:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

Only local settings tests:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~LocalAppSettingsStoreTests"
```

## 4. Schedule Config Test Notes

Schedule config is saved under:

```json
{
  "Sync": {
    "Schedule": {
      "Enabled": true,
      "Frequency": "Weekly",
      "TimeZoneId": "UTC",
      "RunTimes": ["08:00", "17:30"],
      "DaysOfWeek": ["Monday", "Friday"],
      "DaysOfMonth": [1, 15],
      "RunOnLastDayOfMonth": false,
      "PreventOverlappingRuns": true
    }
  }
}
```

Invalid schedules must not be saved. Examples:

- weekly schedule without selected weekdays
- monthly schedule without `DaysOfMonth` and without `RunOnLastDayOfMonth`
- monthly days outside `1..31`
- duplicate run times

## 5. Manual UI Verification

Manual TrayApp verification:

```powershell
dotnet run --project .\src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj
```

Routine UI smoke check:

- Atera API key input is masked
- entering test values and clicking `Save Config` shows success
- `C:\ProgramData\AteraSnipeSync\appsettings.local.json` contains reusable `Atera`, `SnipeIt`, and `Mapping` settings, including `Atera.ApiKey` and `SnipeIt.ApiToken`
- close and reopen TrayApp; confirm the masked credential fields and the other settings are restored from the local file without printing keys or tokens in the log
- do not use a real API key for routine UI smoke testing

Current temporary no-service manual sync check:

- Run the TrayApp command above.
- Enter the Atera API base URL, normally `https://app.atera.com/api/v3`.
- Paste the Atera API key into the masked field.
- Enter the real Snipe-IT API base URL including `/api/v1`; do not leave example values such as `https://snipe.example.com/api/v1`.
- Paste the Snipe-IT API token into the masked field.
- Click `Test Atera` to validate the Atera base URL and API key through the existing read-only inventory pull path.
- Click `Test Snipe-IT` to validate the Snipe-IT base URL and token through `GET /hardware?limit=1`.
- Fill mapping defaults: company, manufacturer, model, category, and default status id.
- Add company aliases when Atera and Snipe-IT names differ, one per line, for example `Moore Equine Veterinary Centre - AR=Moore Equine Veterinary Centre`.
- Leave create-missing options unchecked for the first safety preview unless the operator intentionally wants to test reference creation.
- Click `Save Config` if these values should be reused the next time the manual window opens.
- Click `Load Config` after editing saved local settings or moving a config file onto the machine; confirm the panel reloads the saved values.
- Click `Preview Changes`.
- Confirm the progress area changes from idle to a percentage and shows current work such as Atera page pull, mapping, Snipe-IT planning, dependency lookup, or CSV writing.
- Confirm the UI log shows only `Starting sync.`, `Processing models.`, `Processing categories.`, `Processing assets.`, and `Completed.` for a successful run; it must not show agent/asset names, counts, progress fractions, failure details, or local paths.
- Confirm `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log` exists for the current local date and contains every sanitized progress callback, detailed summary, and saved `SyncResult_*.json` path as separate lines.
- For a failed Preview/Sync, confirm `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_Error_yyyyMMdd.log` contains one failure block with every grouped reason and the history report path; confirm a successful run does not add a block and no key/token/raw payload appears.
- Open the CSV folder only on the local machine and inspect the generated CSV files.
- Click `Sync Now` only after the preview output has been reviewed; the UI must show a confirmation dialog first.
- If a sync fails, confirm the daily/error file logs contain `Failure x<count> ... Affected: ...`, list different root reasons separately with the largest occurrence count first, and preserve readable field-level Snipe-IT validation reasons without raw JSON or secrets.

Manual real-key verification rules:

- real API keys must be entered manually by the owner/operator only; after `Save Config`, they remain in the local plaintext test file and do not need to be re-entered on each launch
- do not print, screenshot, log, commit, or paste keys/tokens into tracked files
- do not add this flow to `dotnet test`, build, CI, or scripted probes
- expected safe UI log output is the five milestone lines only; counts, warnings, failures, CSV paths, and history paths remain in sanitized local files
- expected saved output is local-only history JSON and date-split log files under `C:\ProgramData\AteraSnipeSync`
- `Test Atera` is read-only and uses a bounded one-item/one-page probe
- `Test Snipe-IT` is read-only and must not create, update, or delete records
- Snipe-IT token is persisted only in the local machine config when the operator clicks `Save Config`
- `Preview Changes` is dry-run for writes, but it still contacts Snipe-IT for lookup planning
- if `Save Config` is clicked, panel values are saved only in `C:\ProgramData\AteraSnipeSync\appsettings.local.json`

Cleanup after manual real-key verification:

- close the TrayApp window
- clear or delete `C:\ProgramData\AteraSnipeSync\appsettings.local.json` if a real Atera key was saved
- remove temporary preflight folders under `C:\ProgramData\AteraSnipeSync\Preflight\` when they are no longer needed
- remove local manual sync log files only when they are no longer needed for operator troubleshooting
- clear shell history only if commands included sensitive values

Future production manual sync UI should still show the preflight CSV folder and allow confirm/cancel through the formal TrayApp/WorkerService path. Scheduler automatic sync must not show that UI, must not wait for confirmation, and must not generate manual preflight CSV files.

## 6. 安全规则

- automated tests must not read real API keys
- automated tests must not write to `C:\ProgramData`
- automated tests must not call real Atera or Snipe-IT APIs
- real API keys may only be entered manually by the owner/operator
- the Manual Sync test-stage `appsettings.local.json` stores credentials in plaintext and must be treated as sensitive
- `appsettings.local.json` must not be committed to git, copied into a package, or included in support logs
- WorkerService credential behavior is intentionally outside this Manual Sync test flow

## 7. Ignore MAC Addresses 配置验证

- `SaveManualSyncSettingsAsync_SavesIgnoredMacAddresses` 验证常见 MAC 格式被规范成冒号大写形式、按实际地址去重并保存到 `SnipeIt.IgnoredMacAddresses` 数组。
- `SaveManualSyncSettingsAsync_ThrowsArgumentException_WhenIgnoredMacIsInvalid` 验证无效 MAC 不会写入 config。
- 手动 UI 验证时，在 `Ignore MAC Addresses (; separated)` 输入 `00:09:0F:AA:00:01; 00:09:0F:FE:00:01`，点击 `Save Config`，重开窗口后应按相同顺序回填。

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~LocalAppSettingsStoreTests"
```
## 2026-07 Normalize Categories 手动验证（已由统一 Preview/Sync 流程取代）

以下内容保留为历史兼容入口记录。当前 UI 不再要求先点击独立 Normalize 按钮；正式验证以本手册后面的 “Default Category + MAC Fieldset 统一流程” 为准。

WinForms 自动测试不点击 UI，也不调用真实 API。先运行 Core mocked tests 与 solution build：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeModelCategoryNormalizerTests"
dotnet build AteraSnipeSync.sln --no-restore
```

需要 owner/operator 在真实 Snipe-IT 手动验证时：

1. 先完成 Snipe-IT 数据库备份，并确认 API token 具备读取 models/categories 与更新 models 的权限。
2. 启动 TrayApp Manual Sync window；填写真实 Snipe-IT `/api/v1` URL 与 token，把 `Default Category` 设为已存在的 asset category（默认 `Computer`），并在 `Normalize From Categories (;)` 填入允许改变的来源 categories（默认 `Server; Laptop; Desktop`）。
3. API token 只能填入 password textbox 或本机 `C:\ProgramData\AteraSnipeSync\appsettings.local.json`；不得打印、粘贴到日志、截图、commit 或 tracked config。
4. 点击 `Normalize Categories`。先核对确认框中的来源列表、scanned Model、candidate Model 和 target category 数量；candidate 只能来自文本框列出的 categories。Printer、Network Equipment 等未列出 categories 不应出现。不确定时选择 No，此时不应出现 PUT。
5. 选择 Yes 后等待完成；UI 应显示 updated/failed/cancelled 汇总和 `Category normalization log` 绝对路径。
6. 打开 `C:\ProgramData\AteraSnipeSync\Logs\ModelCategoryNormalization_*.log`，确认每个 candidate Model 只有一条 PLAN 和一条 SUCCESS/FAILED，包含 Model ID/name、旧/新 category，但不含 token、Authorization header、raw payload 或 raw response。
7. 在 Snipe-IT UI 抽查受影响 Model：Category 已变为 `Computer`，引用该 Model 的所有 assets 仍使用原 Model；没有新建/删除 Model、Category 或 Asset。
8. 再运行 `Preview Changes`，确认文本框所列来源 categories 的 mismatch 已消失，同时抽查未列出的 inventory Model category 完全未改变。

安全输出示例只应类似：

```text
SCAN complete. Models scanned=120; source categories=Desktop, Laptop, Server; candidate models=14; target category=Computer (id 3).
SUCCESS; model id=40; name='PowerEdge R740'; category='Server' -> 'Computer'.
Category normalization summary: updated models=14; failed models=0; canceled=False.
```

清理：本流程不创建 session environment variables 或临时 probe 文件。测试完成后关闭 TrayApp；专用日志作为审计记录保留。若本机不再用于手动测试，可由 owner 删除本地敏感 `appsettings.local.json`；不得把它复制进仓库。失败或部分取消时不要重做数据库还原式猜测，先根据专用日志核对成功的 Model IDs，再决定是否再次运行（再次扫描会自然跳过已变为 Computer 的 Model）。

## 2026-07 单一 Category 与 DeviceType Preview 检查

自动验证：

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalAppSettingsStoreTests|FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~SnipeImporterTests"
dotnet build AteraSnipeSync.sln --no-restore
```

手动 UI 检查不使用测试中的 fake key：

1. 打开 Manual Sync，确认只有一个 `Default Category=Computer` 输入。
2. Load 双字段配置时确认 UI 使用 `DefaultComputerCategoryName` 回填单一输入；Save 后检查 JSON 只含 `DefaultCategoryName`，且不得在日志打印 token。
3. Preview 一个 Atera `DeviceType=Server` 的设备，确认 assets CSV 最后一列为 `DeviceType` 且值为 `Server`。
4. 当该 Server 设备的同名 Snipe Model 位于 Server category 时，确认 Preview 期望 Computer、显示 `SnipeImport.ModelCategoryMismatch`，不执行真实写入。
5. `Normalize Categories` 使用同一个 Default Category，但只把 `Normalize From Categories (;)` 列出的非目标 Models 纳入确认计划。

## 2026-07 Manual Sync 逐条文件日志与简化 UI 检查

真实 API 验证仍只允许 owner/operator 手动执行。先通过 Preview 确认存在 missing Company/Category/Model，再运行 Sync Now：

1. UI log 成功 run 只能按顺序出现 `Starting sync.`、`Processing models.`、`Processing categories.`、`Processing assets.`、`Completed.`；每个阶段只出现一次。
2. UI log 不得出现 Atera agent 名称、Snipe-IT asset 名称、`(Current/Total)`、逐笔 reference、count、CSV/report 路径或 failure detail；progress bar 的当前 detail label 可继续显示安全的即时细节。
3. `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log` 必须保留每个 progress callback。抽查连续 agent/asset entry，确认不再每隔若干项抽样；两笔间隔小于 3 秒也必须分别存在。
4. 每笔 reference 必须在文件中连续出现 `Creating ... (i/N)` 与 `Created ... in X.XXXs (i/N)`；失败时保留 `Failed ... after X.XXXs (SnipeImport.<Code>)`。
5. 关闭 TrayApp 后重新读取日志，确认关闭前已接受的 entry 已全部 flush。所有行不得包含 API token、Authorization header、raw payload 或 raw response。

## 2026-07 Manual Sync 加权进度检查

自动验证：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualSyncProgressCalculatorTests"
```

真实 Sync 手动检查：

1. model/category/reference preparation 完成时百分比不得超过 15%；hardware/model snapshot 自身 `Current == Total` 也不得跳到 95%。
2. 进入 `Executing Snipe-IT asset 1/485` 时应约为 35%，不是 95%。
3. 执行到约 `243/485` 时应约为 67%，随后随每个 asset 平滑增加。
4. `485/485` 后应为 99%，只有整个 Sync run 返回最终结果后才显示 100%。
5. Preview 不执行 writes；其 485 个 matching/planning entry 应覆盖 15–95%，CSV/dry-run finalize 覆盖 95–99%。

本检查不使用额外环境变量或临时文件。不要为了制造失败而在真实环境提交无效 mutation；失败显示由 mocked test 覆盖，真实环境只观察自然结果。

## 2026-07 Default Category + MAC Fieldset 统一流程手动验证

自动验证：

```powershell
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
```

Manual Sync UI 检查：

1. 确认窗口包含可编辑的 `MAC Fieldset Name` text field；名称由 operator 填写，不假定 `Assets with MAC Address` 是 Snipe-IT 官方固定值。
2. `MAC Fieldset Name` 与 `MAC Custom Field DB Column` 必须同时填写或同时留空；只填一个时 Save/Preview/Sync 应在请求前提示配置错误。
3. 将 `Default Category` 设为实际目标 category，例如 `Computer` 或其它自定义名称。MAC Fieldset 检查只适用于该 category 下的全部 Model，以及本次从 `Normalize From Categories (;)` 归一化进入该 category 的 Model。
4. 点击 `Preview Changes`，检查 `snipeit-models-plan.csv`：
   - 默认 category 下缺少目标 Fieldset 的现有 Model 显示 `Modify`，包括当前 Atera batch 未引用的 Model。
   - 其它 category（例如 Printer、Network Equipment）不因缺少该 Fieldset而出现 `Modify`。
   - 同时需要 category normalization 和 Fieldset assignment 的 Model 只出现一条合并 `Modify`。
   - 新建到默认 category 的 Model 在 Add row 中显示目标 Fieldset。
5. 只有确认 Preview 后才点击 `Sync Now`。真实运行应先完成 Model create/update，再开始 Asset create/update；同一 Model 的 category + Fieldset 变更只能有一次 PATCH。
6. 完成后再次 Preview；已正确绑定 Fieldset 且 category 已归一化的 Model 不应再次显示 Modify。

真实 token 仍只能由 owner/operator 在本机 UI 输入，不得写入测试、日志、截图或 tracked files。

## Manufacturer Alias UI 验证

1. 在 `Manufacturer Aliases` 输入 `Dell Inc.=Dell` 并保存配置。
2. 重新加载配置，确认该行仍存在；对应 JSON 路径为 `Mapping.ManufacturerAliases`。
3. Preview CSV 的 `ManufacturerName` 应显示 `Dell`，不再显示 `Dell Inc.`。
4. 输入 `Dell Inc.=>Dell`、多个 `=` 或任一侧为空时，Preview/Sync 必须在 API run 前拒绝。
5. TrayApp solution build 用于验证新增文本框、parser、settings 与 request 接线；不启动 GUI，也不调用真实 API。
