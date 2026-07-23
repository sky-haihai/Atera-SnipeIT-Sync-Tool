# Project Progress

Last updated: 2026-07-23

## 2026-07-23 Notification deleted count projection

Production code changed:

- added required `NotificationRequest.Deleted` so senders receive a prepared structured count instead of parsing human-readable message text.
- changed `NotificationRequestFactory` to project `SnipeImportResult.DeletedAssets` and add `DeletedAssets: <count>` to the shared Email/Teams message summary.
- added numeric top-level `deleted` to the `GenericJson` webhook payload. The Teams Adaptive Card root envelope remains unchanged and displays the count through its safe message TextBlock.
- Test Notifications explicitly uses `Deleted = 0`; scheduled, manual, preview and fallback sync notifications use the real importer count through the shared factory.

Automated tests changed:

- extended factory coverage to assert `Deleted == 3` and the `DeletedAssets: 3` message line.
- extended the fake-handler generic webhook contract test to assert `deleted` is a JSON number with value 3 while retaining endpoint/config secret-exclusion assertions.
- updated all direct `NotificationRequest` fixtures for the required deleted metric. Tests remain offline and did not contact SMTP, webhook, Atera or Snipe-IT endpoints.

Documentation changed:

- updated Notification functional responsibilities and technical specification before production code with the deleted-count source, generic JSON wire shape, Teams boundary and required tests.
- updated the Notification unit-test guide after implementation with the actual factory and fake-handler assertions.

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~Notification" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --nologo
dotnet test AteraSnipeSync.sln --no-build --no-restore --nologo --logger "console;verbosity=minimal"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --nologo --logger "console;verbosity=minimal"
```

Latest known result:

```text
Notification tests: 43 passed, 0 failed
Build: succeeded, 0 warnings, 0 errors
First full run: 314 passed, 1 transient named-pipe timing failure
Focused named-pipe rerun: 1 passed, 0 failed
Final full solution rerun: 315 passed, 0 failed, 0 skipped
```

Remaining module gaps / next steps:

- no real webhook or SMTP notification was sent. Deploying operators should verify one sanitized notification in their own environment without copying the private webhook URL or SMTP credentials into logs or issues.
- consumers of `GenericJson` must accept the new numeric `deleted` field. Teams Workflow consumers receive the established Adaptive Card envelope and require no root-schema change.

## 2026-07-23 Atera-managed stale asset soft-delete and audit

Production code changed:

- added run-local comparison between the complete Snipe-IT Hardware snapshot and every current mapped Atera asset tag. Only normalized tags beginning with `ATERA-` are owned by automatic sync; other assets are never delete candidates.
- added deterministic stale candidates ordered by Snipe-IT asset id. An empty successful Atera batch deletes every `ATERA-` asset, while any source validation/reference/matching/duplicate-target failure disables all deletes for that run as a fail-closed safety gate.
- added official `DELETE /hardware/{id}` soft-delete execution with no request body and no mutation retry. Dry-run and Preview create planned Delete actions/CSV rows without sending DELETE.
- added `SnipeImportResult.DeletedAssets` and `ImportAction.Identifier`. Successful deletes increment the real count only after HTTP and Snipe-IT business-envelope success; one failed delete records a target-specific failure and does not block later candidates.
- added structured Information/Warning deletion logs containing Snipe-IT asset id, asset tag, device name, parsed Atera Agent ID, `MissingFromAtera` reason and result. Logs/actions do not include API token, raw response body or full notes.
- changed Status Store `assetsDeleted` and Worker IPC `Deleted` from fixed zero to the importer count. History `assets.deleted` now retains the asset id in `identifier` plus the safe audit message.

Automated tests changed:

- added stale/current/manual ownership coverage, case-insensitive/trimmed tag protection, no-body DELETE verification, dry-run CSV/no-mutation coverage, empty-Atera managed deletion, malformed-snapshot fail-closed behavior, per-delete failure continuation, and deletion log secret-exclusion assertions.
- added Status Store coverage for real deleted count, deleted action, identifier and audit message.
- changed Worker summary coverage to project a non-zero `DeletedAssets` value while continuing to exclude raw Atera records.
- all HTTP remains mocked through local handlers; automated tests did not call real Atera or Snipe-IT APIs.

Documentation changed:

- updated the master plan and module 03 functional responsibilities/technical specification before implementation with the `ATERA-` ownership boundary, official soft-delete endpoint, execution order, fail-closed gates, counters, logs and required tests.
- updated module 05 and 07 responsibilities/specifications for real history and IPC deleted-count projection; updated the Tray specification to display the real count.
- updated Snipe Import, Status Store and Worker unit-test guides with actual mocked cases and commands.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~JsonFileSyncStatusStoreTests|FullyQualifiedName~WorkerCommandHandlerTests"
dotnet test AteraSnipeSync.sln --no-restore
```

Latest known result:

```text
Build: succeeded, 0 warnings, 0 errors
SnipeImporter tests: 89 passed, 0 failed
Status Store + Worker command tests: 35 passed, 0 failed
Full solution: 315 passed, 0 failed, 0 skipped
```

Remaining module gaps / next steps:

- no real-key validation was run. Before first deployment, use `Preview Changes` with local-only credentials and review every `Delete` row; never print, log or commit either API token.
- the `ATERA-` asset-tag namespace is now the explicit ownership contract. A manually created Snipe-IT asset using that prefix will be treated as auto-managed.

## 2026-07-22 Scheduler, snapshot, statistics, and IPC hardening

Production code changed:

- replaced arbitrary-length scheduler delays with an immediate UTC due check plus a 15-second `PeriodicTimer` driven by `TimeProvider`.
- added atomic `schedule-state.json` persistence through `ScheduleRuntimeState`, `IScheduleRuntimeStateStore`, `JsonFileScheduleRuntimeStateStore`, and stable schedule-rule fingerprints. Due occurrences are saved as claimed before execution, missed periods coalesce into one catch-up, and disabled rules invalidate stale occurrences.
- kept editable local recurrence rules while making all runtime comparisons and persisted `NextRunUtc`/`LastTriggeredUtc` strict UTC `Z` values. Ambiguous DST times select the later UTC instant and invalid spring-forward times are skipped.
- changed `WorkerRuntimeFactory` to read one complete `SyncAppSettings` snapshot per run and use its `DryRun` and `Notifications` values directly.
- made `SyncRunResult.DryRun` required and aligned history, notification and IPC projections. Run-level Atera/Mapping failures remain structured failures but no longer inflate `AssetsFailed`.
- paged Snipe-IT `/companies` with `limit=500` and `offset`, failing closed on changing totals, premature empty pages, malformed rows, over-full snapshots, or the 10000-page guard.
- decomposed the importer into `SnipeApiClient`, `SnipeSnapshotLoader`, `SnipeImportPlanner`, and `SnipeImportExecutor` responsibility segments while preserving `ISnipeImporter`, public constructors, request payloads, retry rules, and business errors.
- deleted unused Core `SyncScheduler`/`ISyncScheduler`, `SnipeModelCategoryNormalizer`, Tray `ModelCategoryNormalizationLog`, and the obsolete standalone tests.
- added Worker IPC defaults of 16 concurrent connections and a 10-second first-line read timeout, with semaphore and named-pipe instance limits releasing slots after every connection.
- plaintext credential storage and local BuiltinUsers pipe access are intentionally unchanged. Latest-history fallback and Open Log Folder behavior are also unchanged for this iteration.

Automated tests changed:

- added UTC state round-trip/corruption tests, 61-day monthly recurrence, Edmonton DST fixed-time behavior, ambiguous/invalid clock cases, overdue restart catch-up, missed-period coalescing, fingerprint and disable/re-enable invalidation, and claim-before-run ordering.
- added one-snapshot runtime construction checks, history/notification/IPC failure-count checks, and required `DryRun` propagation on orchestrator early returns.
- added mocked 501-company pagination, stable offsets, empty-page, changing-total, and maximum-page tests. Automated tests do not call real Atera or Snipe-IT APIs.
- added named-pipe first-line timeout, long-command, connection-cap release, and retained local-user connectivity tests.

Documentation changed:

- updated module plans and technical specifications for modules 03, 05, 06, 07, and 08 before/alongside implementation.
- updated test guides for modules 03 through 08 with the implemented offline test cases and commands.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore --configuration Debug --verbosity minimal
dotnet build AteraSnipeSync.sln --no-restore --configuration Release --verbosity minimal
dotnet test AteraSnipeSync.sln --no-build --no-restore --configuration Debug --verbosity minimal
dotnet test AteraSnipeSync.sln --no-build --no-restore --configuration Release --verbosity minimal
git diff --check
```

Latest known result:

```text
Debug build: succeeded, 0 warnings, 0 errors
Release build: succeeded, 0 warnings, 0 errors
Debug tests: 305 passed, 0 failed, 0 skipped
Release tests: 305 passed, 0 failed, 0 skipped
git diff --check: passed; repository line-ending conversion warnings only
```

Remaining next step: deployment-only smoke verification may confirm the Windows Service account can create `%ProgramData%\AteraSnipeSync\schedule-state.json` and that Tray schedule reload reaches the installed Worker. No real API, notification endpoint, SCM mutation, or real credential was used by automated verification.

## Latest Update

Snipe-IT asset Preview/Sync 现在只把真正需要写入的 existing assets 视为 Modify；完全一致的资产成为 no-op Skip，不再充满 `snipeit-assets-plan.csv`，也不会发送无意义的 hardware PATCH。

Code changed:

- 扩展 hardware snapshot contract，保留 nested `company.id`、`model.id` 与 `status_label.id`，并继续读取 name、asset tag、serial、notes 与 MAC custom field。
- 新增统一 asset change detection，比较实际 payload 管理的 `asset_tag`、`status_id`、`model_id`、`company_id`、name、可靠 serial、实际选中的 MAC 与业务 notes；Preview 与真实 Sync 共用同一判断。
- `First added...` 与 `Auto Synced...` 审计时间行不再单独触发 Modify；已有 first-added marker 在真正更新时仍保留。
- snapshot 缺少必要 relation id 时 fail safe 为 Modify，避免无法证明相等时误跳过更新。
- 完全一致的 existing asset 增加 `SkippedAssets` 和结构化 `Skip/Asset` action；真实 Sync 不发送 `PATCH /hardware/{id}`，也不把 no-op 计入 executed HTTP writes。
- `snipeit-assets-plan.csv` 只输出 Add、真正的 Modify 与 Blocked；新增 `ChangeReasons` column，Add 显示 `Create`，Modify 使用稳定字段顺序说明原因，空计划仍写 header。

Tests changed:

- 新增 Preview unchanged/header-only CSV、真实 no-PATCH Skip、stable multi-field ChangeReasons、missing snapshot ids fail-safe，以及 unreliable serial/ignored MAC 不制造虚假变更的 mocked HTTP tests。
- hardware fixture 默认使用包含 company/model/status nested ids 的 snapshot shape，并可显式省略 ids 测试不完整 response。
- 自动测试继续只使用 `StubHttpMessageHandler`，未调用真实 Snipe-IT API。

Documentation changed:

- 更新 `03-SnipeImport-功能职责.md`，明确 asset plan CSV 是真实写入/阻塞计划、审计时间不驱动写入及 no-op Skip 边界。
- 更新 `03-SnipeImport-技术规格.md`，记录 snapshot fields、change method、reason ordering、CSV header、counter/action semantics 与必需 tests。
- 更新 `03-SnipeImport-单元测试指导手册.md`，记录新增用例、mocking shape 与人工 Preview 验收项。
- 实现前核对官方 Snipe-IT hardware list、hardware update 与 custom-field 文档；本轮未新增 endpoint 或真实 API probe。

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests" --verbosity minimal` -> passed 79/79.
- `dotnet test AteraSnipeSync.sln --no-restore --verbosity minimal` -> passed 275/275.
- `dotnet build AteraSnipeSync.sln --no-restore --verbosity minimal` -> succeeded with 0 warnings and 0 errors.
- `git diff --check` -> passed; only existing LF-to-CRLF conversion notices were reported.

Remaining gaps / next steps:

- 部署新 build 后重新运行 `Preview Changes` 生成新的 CSV；旧的 `snipeit-assets-plan.csv` 不会被追溯重写。
- 可用本地凭据做人工 Preview 验收：确认 unchanged assets 不出现、Modify 都有 `ChangeReasons`。API token 必须只保存在本地配置，不得打印、写入日志、提交或用于自动测试。

## Previous Update

TrayApp 已从 Manual Sync 测试程序转换为正式的 single-instance Tray Dashboard，并通过本机 IPC 管理独立运行的 WorkerService。

Code changed:

- 新增 `TrayApplicationContext`、`TraySingleInstanceGuard` 和 `TrayDashboardForm`：单击或双击 NotifyIcon 只打开一个 Dashboard；关闭窗口仅隐藏，退出 Tray 不停止 WorkerService。
- Dashboard 分别显示 Windows Service 与 Worker IPC 状态、当前操作、schedule reload/next-run、进度和最近结果，并提供 Config、Test Connections、Preview、Sync Now、Cancel、Restart Service 与 Re-register & Restart。
- 新增纯状态归约器 `DashboardStateReducer`：saving/reload、任意 Worker run 和 service maintenance 期间统一禁用冲突操作；Cancel 只对 Tray 发起的 active request 可用。
- 新增 versioned JSON Lines `WorkerIpcClient`：校验 request id、event ordering、terminal event 与消息上限；`Cancel` 使用独立 pipe 精确指向原请求。
- 新增 `SyncConfigurationForm`，覆盖 API、mapping/import、schedule 与 notifications。完整 JSON 通过一次原子保存写入，保存成功后发送 `ReloadSchedule`，失败不回滚配置且不会误报 schedule 已应用。
- 新增 fixed-identity service maintenance：`Restart Service` 与 `Re-register & Restart` 通过 elevated self-helper、固定参数和 `ProcessStartInfo.ArgumentList` 操作 `AteraSnipeItAutoSync`；申请 UAC 前先验证同目录 Worker EXE。
- 新增 read-only latest-history fallback；SCM Running 但 IPC Offline 时 Dashboard 明确区分两种状态，不把 service started 误报为 Worker healthy。
- 删除 `ManualSyncForm`/`SettingsForm`，并将 progress/logging helper 与 Core config contract 从 Manual Sync 命名迁移为正式 Tray/Sync 命名。
- Core `SyncAppSettings` 现在一次承载完整 API、mapping/import、dry-run、schedule 与 notification 配置；Worker 每次 run 仍从该共享 JSON 创建不可变配置快照。

Tests changed:

- 新增 `DashboardStateReducerTests`、`WorkerIpcClientTests`、`ServiceMaintenanceTests` 与 `LatestSyncHistoryReaderTests`。
- 覆盖 Dashboard 控件矩阵、真实本机 Named Pipe event ordering/Cancel、fixed service identity、UAC 取消、Worker EXE preflight、maintenance failure stage 和 history fallback。
- 更新配置、Worker、progress 与 logging 测试以使用 `SyncAppSettings` 和新的正式类型名。
- 自动测试全部使用 fake process/service runner、临时 pipe/目录与 mocked HTTP；未操作真实 Windows Service，也未调用真实 Atera/Snipe-IT API。

Documentation changed:

- 更新 TrayApp 功能职责与技术规格，明确 single-instance Dashboard、完整配置保存后 `ReloadSchedule`、全局 non-overlap 控件状态、Cancel 边界和 elevated service-maintenance 流程。
- 重写 TrayApp 单元测试指导手册，记录实际测试文件、命令、mocking strategy、自动化覆盖边界和人工验收步骤。
- 校准 Worker 技术规格中的完整配置保存接口名称。

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~TrayApp"` -> passed 34/34.
- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalAppSettingsStoreTests|FullyQualifiedName~WorkerService"` -> passed 38/38.
- `dotnet test AteraSnipeSync.sln --no-build --no-restore` -> passed 270/270.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.

Remaining gaps / next steps:

- 在发布目录并排放置 TrayApp 与 `AteraSnipeSync.WorkerService.exe` 后，人工验证 UAC、真实 service re-registration/restart、SCM/IPC health recovery 和 Tray 退出后的独立 scheduled run。
- 使用本地配置人工检查 Dashboard、Config tabs、保存后 next-run 更新、Preview/Sync/Cancel 与最近结果显示；真实凭据必须保持在本地且不得进入日志或版本控制。
- 后续可补正式应用图标、安装/发布打包与更精细的 UX polish；这些不影响当前 Dashboard/Worker contract。

## Previous Update

WorkerService 模组已从 scaffold 升级为可独立运行的正式 scheduler + local IPC host。

Code changed:

- 固定 Windows Service identity：service name `AteraSnipeItAutoSync`、display name `Atera Snipe-IT Auto Sync`、Worker executable `AteraSnipeSync.WorkerService.exe`；Worker host 使用固定 service name。
- 新增 `WorkerScheduleManager` 与 `WorkerScheduler`：启动时读取 schedule，`ReloadSchedule` 原子替换 future schedule/next-run，reload 不取消 active run，Tray 退出后 Worker 仍独立调度。
- 新增 process-wide `SyncRunCoordinator`，Scheduled、Connection Test、Preview、Sync Now 共用一个 non-overlap lease；busy operation 不 queue。
- `WorkerRuntimeFactory` 每次 run 都从共享 JSON 重读完整配置与当前 plaintext credentials，验证后创建独立 runtime；不再使用 environment credential fallback，也不在 runtime construction 时发出网络请求。
- 新增 combined `TestConnections`：Atera 使用 bounded one-page/one-item read probe，Snipe-IT 使用 documented `GET /api/v1/hardware?limit=1` Bearer probe；两端分别返回脱敏结果。
- 新增 version 1 JSON Lines Named Pipe IPC，支持 `Ping`、`GetStatus`、`ReloadSchedule`、`TestConnections`、`PreviewChanges`、`SyncNow`、`Cancel`，并发送 Accepted → Progress → terminal events。
- Named Pipe ACL 允许 SYSTEM/Administrators/Builtin Users、拒绝 Anonymous；连接后使用 Win32 client-computer check 拒绝远端来源，并正确识别本机 `ERROR_PIPE_LOCAL (229)`。
- IPC request 有 protocol version、request-id allow-list 与 32 MiB message limit；invalid/unterminated JSON 在 dispatch 前拒绝。IPC/status/result projection 不包含 credentials、raw Atera records、HTTP body 或 serialized request frame。
- `Cancel` 只取消匹配的 Tray-started request；scheduled run 不进入 cancellable request registry。latest status 保存失败不会覆盖已产生的 structured run result，并会返回明确 warning。
- WorkerService 与 tests 改为 Windows target；tests 直接引用 WorkerService 以覆盖真实本机 Named Pipe transport。

Tests changed:

- 新增 `SyncRunCoordinatorTests`、`WorkerScheduleManagerTests`、`WorkerSchedulerTests`、`WorkerRuntimeFactoryTests`、`WorkerConnectionTesterTests`、`WorkerCommandHandlerTests` 与 `WorkerIpcServerTests`。
- 覆盖 schedule reload/version/next-run、global busy/lease release、每次运行重读配置、combined connection outcome、Cancel target、legacy split connection command rejection、result sanitization、status-save failure 和真实 pipe event ordering。
- 更新 `LocalAppSettingsStoreTests`，验证 Worker 从临时 JSON 读取 plaintext credentials 且读取不改写文件。
- 所有 HTTP 测试使用 fake client 或 mocked handler；未调用真实 Atera/Snipe-IT API，也未操作真实 Windows Service。

Documentation changed:

- 更新 Worker/Tray 功能职责与技术规格，固定职责边界、IPC command、Dashboard control state、schedule save → `ReloadSchedule` 和 service-maintenance contract。
- 将 Worker 技术规格校准为实际 `WorkerScheduler`、`IWorkerRuntimeFactory`、`WorkerSyncResultSummary`、pipe options 与 local-client validation 实现。
- 重写 Worker 单元测试指导手册，记录实际测试文件、命令、mocking strategy、安全边界和后续人工验收步骤。
- 实现前查阅 Atera 与 Snipe-IT 官方 API 文档，确认本轮只新增 read-only connection probes；未改变现有 sync API DTO/payload/pagination contract。

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkerService|FullyQualifiedName~SyncRunCoordinator|FullyQualifiedName~LocalAppSettingsStoreTests.LoadWorkerSyncSettings"` -> passed 20/20.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 244/244.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.
- `git diff --check` -> passed; only existing LF-to-CRLF conversion notices were reported.

Remaining gaps / next steps:

- TrayApp 尚未切换到新的 IPC client/Dashboard；下一模组应实现 `TrayDashboardForm`、`SyncConfigurationForm`、save 后 `ReloadSchedule`、run/reload/maintenance button state 和 Cancel wiring。
- Tray 的 `Restart Service`、`Re-register & Restart`、UAC helper 与 fixed service executable registration 尚未实现；自动测试必须继续使用 fake process/service controller，真实 service 注册只做人工验收。
- 旧 Manual Sync UI 与少量 Core naming（例如 `ManualSyncSettings`/manual preflight helper）暂时保留兼容性，待 Tray 模组迁移时重命名，避免同时破坏已验证的 sync engine。

## Previous Update

Snipe-IT objects first created by this program now carry an immutable UTC creation marker in their notes.

Code changed:

- Added `First added by Atera-SnipeIT Sync Tool at {UTC timestamp}` to every current create payload: Company, Asset Category, Model, and Hardware Asset.
- Extended the hardware snapshot model to retain Snipe-IT notes so later asset updates preserve the original first-added marker instead of replacing it with the latest sync time.
- Existing assets without the program marker are not backfilled with a guessed creation time; they continue to receive only the normal latest `Auto Synced from Atera at {UTC timestamp}` line.
- Existing Model category/Fieldset updates remain scoped to their existing fields and do not replace Model notes. Manufacturer remains lookup-only and is not created by this program.

Tests changed:

- Added `ImportAsync_AddsFirstAddedNoteToEveryCreatedObject` for the Company, Category, Model, and Hardware POST payloads.
- Extended the asset notes test to verify the original first-added marker survives a later PATCH.
- Added `ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker`.
- All tests continue to use mocked HTTP handlers; no real Atera or Snipe-IT API was called.

Documentation changed:

- Updated the Snipe Import module responsibilities, technical specification, and unit-test guide with the creation-marker contract, supported object types, timestamp format, preservation behavior, and mocked test coverage.
- Consulted the official Snipe-IT API documentation and official Snipe-IT source. The OpenAPI explicitly documents hardware `notes`; the current official Company, Category, and AssetModel definitions also expose `notes` as writable fields.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests.ImportAsync_AddsFirstAddedNoteToEveryCreatedObject|FullyQualifiedName~SnipeImporterTests.ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads|FullyQualifiedName~SnipeImporterTests.ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker"` -> passed 3/3.
- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"` -> passed 73/73.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 224/224.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.
- Automated tests did not call real APIs.

Remaining gaps / next steps:

- Optional owner/operator smoke test against a backed-up Snipe-IT instance: create one missing Company/Category/Model/Asset and confirm all four notes display the same first-added UTC marker. Keep real API credentials local and out of logs and tracked files.

## Previous Update

Manual Sync progress is now weighted by actual work, so completed model/category snapshots cannot consume the progress reserved for slow per-asset writes.

Code changed:

- Added `ManualSyncProgressCalculator`, a per-run monotonic calculator used only by Preview/Sync progress callbacks. Connection tests and category normalization retain their existing generic progress behavior.
- Real Sync weights are now: Atera pull/mapping 0–5%, source validation 5–10%, model/category/reference preparation 10–15%, per-asset matching/planning 15–30%, reference writes 30–35%, per-asset create/update 35–99%, and whole-run completion 100%.
- Preview weights are: initial work 0–15%, per-asset matching/planning 15–95%, CSV/dry-run finalization 95–99%, and whole-run completion 100%.
- Child `Current/Total` values are evaluated only inside their phase. A completed 485-row hardware/model snapshot therefore remains at or below 15% instead of incorrectly setting the whole run to 95%.
- Percentages never move backwards when callbacks repeat or arrive out of phase. Terminal failure and final whole-run completion can reach 100%; import-stage completion stops at 99%.

Tests changed:

- Added five `ManualSyncProgressCalculatorTests` covering a 485-asset real Sync, snapshot isolation, Preview weighting, monotonic out-of-order callbacks, and terminal failure.
- The 485-asset real-Sync assertions verify 35% at execution start, approximately 67% at 243/485, 99% at 485/485, and 100% only after final run completion.

Documentation changed:

- Updated the TrayApp module responsibilities, technical specification, and unit-test guide with the real-Sync and Preview weight tables and manual acceptance checks.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualSyncProgressCalculatorTests"` -> passed 5/5.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 219/219.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.
- `git diff --check -- <changed progress code/docs/tests>` -> passed; only existing line-ending conversion notices were reported.

Remaining gaps / next steps:

- Optional owner/operator smoke test with the 485-agent environment: confirm model/category preparation stays at or below 15%, asset execution starts around 35%, and the bar advances through the long-running asset writes. No real API was called by automated tests.

## Previous Update

Manual Sync now keeps every detailed progress entry in the daily file while showing only stable phase milestones in the App log.

Code changed:

- Split TrayApp logging into UI-only, file-only, and shared paths. Every `SyncProgressUpdate` is now queued to `ManualSync_yyyyMMdd.log` with its full safe detail and optional `(Current/Total)` instead of passing through heartbeat/milestone sampling.
- Added `ManualSyncUiStageTracker`; a successful Preview/Sync UI log now emits only `Starting sync.`, `Processing models.`, `Processing categories.`, `Processing assets.`, and `Completed.` once and in order. Agent/asset names, detailed counts, grouped reasons, CSV paths, and history paths remain file-only.
- Changed `DailyLogWriter` from a bounded `DropWrite` channel to a lossless asynchronous unbounded channel. Disposal completes the channel and waits for all accepted entries to flush; writer failure closes the queue so later writes report failure.
- Detailed completion output now writes every warning and grouped failure reason to the file rather than applying the former UI display limits.

Tests changed:

- Added `ManualSyncLoggingTests` covering a 3,000-entry flush (above the former 2,048-entry capacity), suppression of record-specific agent/asset details, ordered/deduplicated UI milestones, and empty-batch milestone completion.
- Linked the two pure TrayApp logging classes into the unit-test assembly; tests use only a temporary local directory and call no external API.

Documentation changed:

- Updated the TrayApp module responsibilities, technical specification, and unit-test guide to define lossless detailed file logging and the five-line Manual Sync UI policy.
- Replaced the obsolete requirement that per-reference progress appear in the UI; reference details remain complete in the daily file.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualSyncLoggingTests"` -> passed 3/3.
- `dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore` -> succeeded with 0 warnings and 0 errors.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 214/214.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded for Core, TrayApp, Tests, and WorkerService with 0 warnings and 0 errors.
- `git diff --check -- <changed TrayApp logging/docs/tests>` -> passed; only existing line-ending conversion notices were reported.

Remaining gaps / next steps:

- Optional owner/operator smoke test: run Preview Changes and inspect the App log for exactly the five successful milestones, then compare `ManualSync_yyyyMMdd.log` to confirm consecutive per-agent/per-asset progress entries are all present. No real API probe was added to automated tests.

## Earlier Update

The split Computer/Server category design remains withdrawn, while Model normalization is now safely limited to operator-selected source categories.

Code changed:

- Restored the single `MappingOptions.DefaultCategoryName` and `ManualSyncSettings.DefaultCategoryName` contract across Core, TrayApp, WorkerService, scheduled/manual request construction, local settings, and the sample configuration.
- All non-ignored Atera device types, including `DeviceType=Server`, now map to the same default category (`Computer` in UI/sample configuration).
- `DeviceType` remains preserved on import records and as the final preview asset CSV column for audit only; it no longer routes category selection.
- Local settings load the single field first and can temporarily fall back to `DefaultComputerCategoryName` from the withdrawn split design. Saving writes only `DefaultCategoryName` and removes both split fields.
- Added a saved `Normalize From Categories (;)` text field to Manual Sync, defaulting to `Server; Laptop; Desktop`; the value persists as `SnipeIt.ModelCategoriesToNormalize`.
- `Normalize Categories` resolves the existing target Asset Category and paginates the complete `GET /models` snapshot, but only Models whose current category name matches the operator list can become candidates.
- Matching is trim/HTML-decode/case-insensitive. Selected unused Models are included, while Printer, Network Equipment, and every other unlisted inventory category are left unchanged.
- Empty source lists fail before API requests or settings writes. The confirmation dialog and dedicated log include the reviewed source list, scanned count, and candidate count.
- Confirmed execution still updates each candidate through `PUT /models/{id}` with required `name` and `category_id`, continues after independent failures, and never creates/deletes Models, Categories, or Assets.
- Existing preview Model uniqueness/category mismatch gates remain active, so a Server device whose existing Model is still in Server category is blocked before real reference creation until normalization is run.

Tests changed:

- Updated mapping coverage to prove Server, non-Server, and blank DeviceType values all use the single default category while preserving DeviceType.
- Updated Snipe importer preview coverage so `DeviceType=Server` still expects Computer and blocks an existing Server-category Model, with DeviceType retained in the Blocked CSV row.
- Rebuilt normalizer mocked-HTTP coverage around paginated `/models`: default selected categories, unlisted-category skip, custom case-insensitive source lists, empty-list rejection, unused candidates, Computer skip, malformed/conflicting rows, target-category gates, PUT payload, partial failure, no-op, and cancellation.
- Updated local-settings tests for single-category round trip, persisted normalization source lists, empty-list rejection, and fallback migration from the withdrawn split fields.
- All HTTP tests use mocked handlers; no automated test called real Atera or Snipe-IT APIs.

Documentation changed:

- Updated module responsibilities and technical specifications for Reconstruction, SnipeImport, WorkerScheduler, and TrayApp before production code, explicitly superseding the split-category design.
- Updated the corresponding unit-test guides after tests were implemented.
- Rechecked official Snipe-IT `GET /models` pagination and `PUT /models/{id}` required `name`/`category_id` documentation before changing normalizer wire behavior.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Solution build: succeeded with 0 warnings and 0 errors.
Full solution tests: 199 passed, 0 failed, 0 skipped.
Diff check: passed; only LF-to-CRLF conversion notices were reported.
```

Remaining manual-only verification:

- Back up Snipe-IT, load the existing local configuration in TrayApp, confirm `Default Category=Computer` and `Normalize From Categories (;)=Server; Laptop; Desktop`, then save once.
- Run `Normalize Categories`, verify the confirmation includes only those source categories, and audit `PowerEdge R740` plus an unused selected Model.
- Also audit at least one Model in an unlisted category such as Printer and confirm it was not changed.
- Run Preview Changes afterward and confirm Server devices still show `DeviceType=Server` in the final CSV column while their requested category is Computer. Do not run Sync Now until preview has no Model category/name-number conflicts.

## Previous Update

Manual preflight asset CSV now exposes complete MAC and batch-identity conflict evidence.

Code changed:

- Added `MacAddresses`, `ConflictingFields`, `ConflictingValue`, and `ConflictingAssets` columns to `snipeit-assets-plan.csv`.
- `MacAddresses` exports every valid source MAC in normalized colon-separated form, de-duplicated and joined with `; ` for Add, Modify, and Blocked rows.
- Refactored batch identity detection to retain each conflicting field, normalized value, and every peer source record instead of reducing the result to one message string.
- `ConflictingValue` labels each value, for example `MAC address=00:11:22:33:44:55`.
- `ConflictingAssets` identifies each peer as `AssetTag=<tag> | Name=<name> | SourceId=<id>` so duplicate Atera agents and renamed devices remain distinguishable even when they share an asset tag.
- Conflict columns are populated only for `SnipeImport.DuplicateBatchIdentity`; unrelated Blocked rows keep the columns empty while still exposing their MAC list.
- Existing CSV quoting and formula-neutralization behavior applies to the new columns.

Tests changed:

- Added `ImportAsync_WritesMacAndConflictDetailsToPreflightCsv_WhenBatchIdentityCollides`.
- Covered multiple MACs on one asset, normalized alternate MAC formats, asset-tag/MAC/serial collisions, MAC-only multi-peer collisions, peer SourceIds, and write suppression.
- Updated existing CSV row assertions for the expanded column order.
- No automated test called a real Atera or Snipe-IT API.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 44 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 161 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported line-ending conversion warnings only.
```

Operator verification:

- Restart the newly built TrayApp and run Preview Changes again.
- Open `snipeit-assets-plan.csv`; the former MAC-only failures should now name the exact normalized MAC and all colliding Asset Tag/name/SourceId peers.

## Previous Update

TrayApp now writes a dedicated daily error log containing every grouped failure reason for failed Preview and Sync runs.

Code changed:

- Added a second asynchronous `DailyLogWriter` configured for `ManualSync_Error` files under `C:\ProgramData\AteraSnipeSync\Logs`.
- A failed completed run writes one atomic queue entry containing mode, local/UTC timestamps, pull/map/warning/failure counts, every grouped failure reason, and the saved `SyncResult_*.json` history path when available.
- Dedicated error output is not limited to the 10 reasons displayed in the UI and does not include routine progress messages.
- Successful Preview/Sync runs do not add an error block.
- Exceptions raised before a structured run result is available are also written as a standalone sanitized error entry.
- Generalized `DailyLogWriter` with a validated file-name prefix while retaining bounded background writes and 30-day retention.
- TrayApp shutdown now flushes both the normal and dedicated error log writers.

Documentation changed:

- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 160 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported line-ending conversion warnings only.
```

Operator verification:

- Restart the newly built TrayApp and run a failed Preview or Sync.
- Confirm `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_Error_yyyyMMdd.log` contains the complete failure block and history JSON path.
- Confirm credentials, raw request payloads, and raw API response bodies are absent.

## Earlier Update

Real sync no longer plans a duplicate Snipe-IT company when the company snapshot returns an HTML-escaped name.

Diagnosis:

- Reviewed `SyncResult_20260715_180323_8913568Z.json` and the matching TrayApp log for the 490-asset real run.
- 75 assets were independently blocked by `SnipeImport.DuplicateBatchIdentity`.
- The other 415 assets completed reference and hardware planning, but the import stopped before all asset writes when `POST /companies` attempted to create `Ktunaxa Kinbasket Child & Family Services`.
- Snipe-IT rejected that create with `name: The name has already been taken`, proving the company already existed even though the company snapshot lookup had planned it as missing.
- The official Snipe-IT API overview states that API output text is automatically escaped and consumers must unescape it. The importer previously indexed escaped snapshot text such as `&amp;` without decoding it, so it did not match the unescaped Atera company name.

Code changed:

- Changed shared Snipe-IT response string parsing to HTML-decode exactly once before trimming and matching API text.
- Existing company ids are now reused when Snipe-IT returns an escaped company name, preventing the invalid duplicate-company create and the resulting shared reference stop.
- Changed TrayApp failure-reason ordering to show the largest grouped occurrence count first. A shared `x415` reference failure now appears before isolated `x1` identity conflicts instead of being hidden beyond the first 10 reasons.

Tests changed:

- Added `ImportAsync_MatchesHtmlEscapedCompanyName_FromCompanySnapshot`.
- Verified a mocked `Ktunaxa Kinbasket Child &amp; Family Services` snapshot row matches the unescaped source name, sends no `POST /companies`, and uses the existing company id in the hardware create payload.
- No automated test or diagnostic probe called the real Snipe-IT API.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 43 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 160 passed, 0 failed, 0 skipped.
```

Remaining operator validation / next steps:

- Run the newly built TrayApp and repeat Preview. `Ktunaxa Kinbasket Child & Family Services` should no longer appear as an `Add` company.
- Review the remaining 75 `DuplicateBatchIdentity` assets; they remain intentionally blocked, while the other 415 assets can proceed independently once all reference creates succeed.
- The next real sync may expose a later model or asset validation rule that Preview cannot validate without writes; the improved Tray log will put the highest-impact root reason first.

## Earlier Update

The future read-only Health Check module has been recorded as a formal roadmap backlog item.

Documentation changed:

- Added `docs/ROADMAP.md` with the module goal, initial duplicate/reference-integrity rules, proposed finding contract, safety boundaries, open design decisions, and backlog exit criteria.
- Linked the roadmap from `docs/PROJECT_PLAN.md` and added Health Check after the current delivery sequence.
- Added the Health Check/Data Integrity extension to `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` without adding it to the first-version completion criteria.

Scope decision:

- The future module is read-only and reports suspected operator mistakes such as duplicate models, categories, companies, manufacturers, custom fields, and invalid reference relationships.
- It must provide the affected record ids and evidence, never choose, merge, delete, rename, or repair records automatically.
- No production code or automated tests changed in this documentation-only update.

Latest verification command:

```powershell
git diff --check
```

Latest known result:

```text
Documentation diff check succeeded; Git reported line-ending conversion warnings only.
```

## Earlier Update

SnipeImport model ambiguity is now isolated to the duplicated model name/category key instead of blocking every asset that uses the shared model snapshot.

Diagnosis:

- Reviewed the operator-provided `snipeit-assets-plan.csv` containing 490 blocked rows.
- 415 rows inherited one `SnipeImport.AmbiguousModelMatch` for duplicate Snipe-IT model name `Dell pro 14` in category id 10, even though none of those 415 mapped records had `ModelName = Dell pro 14`.
- The remaining 75 rows were independently blocked by `SnipeImport.DuplicateBatchIdentity`: 61 MAC-only conflicts, 9 asset-tag/serial conflicts, and 5 asset-tag/MAC/serial conflicts.
- Root cause for the 415-row fan-out was `SnipeModelLookup.Create`: one duplicate lookup key threw while constructing the snapshot index, and the outer reference planner treated the entire model snapshot as failed.

Code changed:

- Changed `SnipeModelLookup` to retain unique model name/category keys and track ambiguous keys separately.
- Changed lookup behavior so `SnipeImport.AmbiguousModelMatch` is thrown only when an asset actually requests the duplicated model name/category key.
- Unrelated unique model keys from the same paginated snapshot now continue through hardware matching and Add/Modify planning.
- Kept batch identity conflict blocking unchanged; those 75 source-data conflicts remain intentionally blocked.

Tests changed:

- Added `ImportAsync_BlocksOnlyAssetsReferencingAmbiguousModelKey` with one unique model and one duplicated model key in the same mocked snapshot.
- Verified the unique model proceeds to hardware planning while only the asset requesting the duplicated key fails.
- Kept all API-facing tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT `/models` documentation was consulted before changing the run-local model lookup behavior.
- The change does not choose an arbitrary Snipe-IT model when duplicate ids share the requested normalized name/category key.
- A future asset whose mapped model is exactly the ambiguous key remains blocked until the duplicate Snipe-IT model records are resolved.

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 42 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 159 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Re-run Preview Changes with `_snipeit_mac_address_1`; unrelated model keys should now plan normally.
- Review the remaining `DuplicateBatchIdentity` rows before real sync; shared asset tag/serial/MAC identities require an operator-approved source-data or matching policy decision.
- Resolve duplicate Snipe-IT `Dell pro 14` models in category id 10 if that exact model key must be imported later.

## Earlier Update

Snipe-IT real-sync failures now preserve and classify the detailed API reason instead of collapsing object-shaped validation messages into a generic error.

Code changed:

- Changed `SnipeImporter` to parse the official Snipe-IT `messages` string, object, and array error shapes.
- Added field-level validation output such as `asset_tag: ...` and `status_id: ...`, with HTML decoding, de-duplication, and a 2000-character safety cap.
- Added request context to HTTP-derived failures: operation target, HTTP method, and relative endpoint are now included without logging authorization headers, tokens, request payloads, or raw response JSON.
- Added granular failure codes for validation, authentication, authorization, rate limiting, server failures, network failures, timeouts, and generic HTTP failures.
- Changed reference lookup error handling so a Snipe-IT `status = error` response is surfaced instead of being treated as a missing reference.
- Kept the existing all-assets block when a required company/category/model create fails, but the copied failure now identifies the exact reference target and root API detail.
- Changed the manual TrayApp result log to group Snipe Import failures by their pre-orchestration code/root message, display occurrence count plus up to 3 affected target examples, and show up to 10 distinct root reasons.

Tests changed:

- Added `ImportAsync_ReportsFieldValidationDetails_WhenMessagesIsObject`.
- Added `ImportAsync_ClassifiesAuthenticationFailure_WithRequestContext`.
- Added `ImportAsync_ClassifiesServerFailure_WithResponseDetail`.
- Added `ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails` for the shared-reference failure path that blocks every dependent asset.
- Kept all Snipe-IT tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- The official Snipe-IT API overview documents HTTP 200 error envelopes and object-shaped validation messages; that official contract was consulted before implementation.
- Preview does not expose write validation because it intentionally sends no `POST` or `PATCH`. The next real sync will now expose the exact failing reference or asset write and its field-level reason.
- A single failed reference create can still block all assets by design; TrayApp now shows this as one distinct reason with an occurrence count instead of filling the visible list with duplicate asset failures.

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 30 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 146 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Run one operator-controlled real sync to capture the now-detailed Snipe-IT response; do not print or share the API token.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Previous Update

SnipeImport model planning now uses one paginated Snipe-IT model snapshot per import run instead of one API search per unique model reference.

Code changed:

- Changed `SnipeImporter` to load `GET /models?limit=500&offset=...` pages once when at least one planned model belongs to an existing category.
- Added a run-local model index keyed by normalized model name + nested Snipe-IT `category.id`, preserving the previous behavior that existing-model matching does not require manufacturer id.
- Changed the model planning loop to perform local lookups after the snapshot is loaded, removing per-model `GET /models?search=...&category_id=...` requests.
- Preserved the existing new-category flow: when every model category will be created in the same run, the model snapshot is skipped and models are planned for creation after their categories.
- Added shared snapshot failure handling so model references under existing categories are blocked consistently and do not continue to hardware matching.

Tests changed:

- Updated Snipe-IT model fixtures to use the official list-response shape with nested `category.id`.
- Added `ImportAsync_LoadsModelSnapshotPagesBeforePlanning` for advancing model offsets and later-page matching.
- Added `ImportAsync_SharesModelSnapshotAcrossDifferentModelReferences` to prove different model names share one `/models` request page.
- Added `ImportAsync_BlocksExistingCategoryModels_WhenModelSnapshotFails` to verify shared failures stop hardware lookup.
- Kept all API-facing tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI and the official model transformer source were consulted before changing model pagination and response parsing.
- `limit=500` is a page size, not a total limit; pagination advances by the actual number of returned rows until `total` is reached or a page is empty.
- A run with 195 unique model references now pays for model-list pagination once, then resolves those references from memory.
- Company, category, and manufacturer reference lookup behavior is unchanged.

Latest verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests"
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 26 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 142 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Company, category, and manufacturer planning still performs one lookup per unique reference; these can use the same paginated snapshot pattern if they become the next measurable bottleneck.
- Model and hardware snapshots are run-local and are intentionally not persisted across import runs.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Previous Update

SnipeImport name fallback no longer updates assets unless the candidate also matches company, category, and model.

Code changed:

- Changed `SnipeAssetMatch` to retain parsed Snipe-IT hardware `company`, `category` / `asset_category`, and `model` names from the paginated hardware snapshot.
- Changed `SnipeImporter` so MAC matching remains first priority and serial matching remains second priority, but high-similarity name fallback now only considers candidates whose company/category/model all match the mapped import record.
- Kept non-matching name candidates as no-match instead of blocked, so same-name phone assets no longer cause computer imports to be planned as `Modify`.
- Added `MacAddressCustomFieldDbColumnName` and `NameMatchThreshold` to `SnipeItConfig`, and added example values to `samples/configs/appsettings.local.example.json`.

Tests changed:

- Added `ImportAsync_CreatesAsset_WhenNameMatchesButReferencesDiffer` to prove same-name Snipe-IT assets with different category/model are treated as no match and result in `POST /hardware`.
- Updated Snipe-IT asset fixtures to include company/category/model metadata for snapshot-based matching.
- Kept all Snipe-IT tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/technical-specs/03-SnipeImport-æŠ€æœ¯è§„æ ¼.md`
- `docs/test-guides/03-SnipeImport-å•å…ƒæµ‹è¯•æŒ‡å¯¼æ‰‹å†Œ.md`
- `samples/configs/appsettings.local.example.json`
- `progress.md`

Investigation notes:

- MAC matching is disabled when `SnipeImportOptions.MacAddressCustomFieldDbColumnName` is null or whitespace. In that case the importer records `SnipeImport.MacMatchingDisabled` and skips the MAC custom-field index.
- The MAC field value must be the Snipe-IT custom field DB column name, for example `_snipeit_mac_address_5`, not the display label.
- Existing `VUE-00xxx` false positives were allowed because the previous name fallback did not check company/category/model before returning an existing asset.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 139 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Configure the runtime MAC custom-field DB column before real sync runs so MAC matching is enabled.
- Consider surfacing the parsed Snipe-IT candidate company/category/model in future diagnostic output when a name fallback is rejected.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Earlier Update

Company alias matching now tolerates visually equivalent whitespace and dash characters in Atera company names before preview/import planning.

Code changed:

- Changed `MappingValueResolver` so company alias source-key comparison still uses the configured Atera company name as the key, but compares through a normalized form that trims, collapses whitespace, treats non-breaking spaces as spaces, and treats common dash-like characters as `-`.
- Kept alias output one-way: matching `Atera company name` still returns the configured canonical Snipe-IT company name. No bidirectional company lookup was added to Snipe Import.
- Changed the TrayApp company alias editor syntax to display and validate aliases as `Atera company=Snipe-IT company` instead of `Atera company => Snipe-IT company`.

Tests changed:

- Added `Map_UsesCompanyAlias_WhenCustomerNameHasEquivalentWhitespaceAndDash` to prove alias keys match Atera customer names containing non-breaking spaces and an en dash.
- Added `ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName` to run an `InventoryMapper` plus Snipe preview/preflight path using `Moore Equine Veterinary Centre - AR=Moore Equine Veterinary Centre`, verifying no company `Add` row is generated when Snipe-IT already has the alias target.
- Verified the TrayApp alias editor syntax change through a full solution build because the parser lives in the WinForms project rather than the unit-test project.
- Kept all API-facing tests on mocked HTTP handlers only; no real Atera or Snipe-IT API call was added.

Documentation changed:

- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Investigation notes:

- The default local config at `C:\ProgramData\AteraSnipeSync\appsettings.local.json` does contain the expected company aliases, including `Moore Equine Veterinary Centre - AR => Moore Equine Veterinary Centre`.
- A focused preview-style test with exact `Atera Company Name => Company Alias SnipeIT Name` already passed before broadening the key comparison, so the issue was not missing bidirectional comparison.
- The likely miss condition was character-level mismatch between the Atera API customer name and the saved config key, despite the names looking identical in the UI.

Latest verification commands:

```powershell
dotnet test .\AteraSnipeSync.sln --filter "FullyQualifiedName~InventoryMapperTests.Map_UsesCompanyAlias_WhenCustomerNameHasEquivalentWhitespaceAndDash|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName"
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused alias tests passed: 2 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 138 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- If a future preview still shows a company Add row, compare that CSV company name against the Atera customer name in the asset notes to identify non-equivalent wording, not just punctuation/spacing differences.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Earlier Update

SnipeImport asset matching now uses one paginated Snipe-IT hardware snapshot per import run instead of per-asset MAC/serial/name HTTP lookups.

Code changed:

- Changed `SnipeImporter` to load `GET /hardware?limit=500&offset=...` pages once after reference planning and before matching assets.
- Added an in-memory hardware lookup index for configured MAC custom field values and serial numbers, while preserving match priority: MAC, then serial, then high-confidence name.
- Changed name matching to score against the loaded hardware snapshot instead of per-asset `GET /hardware?search=...`.
- Added `SnipeAssetMatch.CustomFields` parsing for Snipe-IT `custom_fields` objects whose entries expose `field` and `value`.
- Added `SnipeImport.AmbiguousSerialMatch` failure handling when the loaded snapshot contains multiple assets with the same serial.
- Preserved the existing behavior that assets blocked during reference planning do not trigger hardware matching work.

Tests changed:

- Updated `SnipeImporterTests` to queue one shared hardware snapshot response instead of per-asset MAC/serial/name lookup responses.
- Added coverage for multi-page hardware snapshot loading with advancing `offset`.
- Added coverage for ambiguous serial matches.
- Kept automated tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI was consulted before changing hardware lookup behavior.
- The change targets preview and real sync planning speed: a 489-asset run should now pay for Snipe-IT hardware pagination once, then match locally in memory.
- Reference lookup behavior for companies, categories, manufacturers, and models is unchanged.
- Hardware writes still happen only after planning/preflight gates; dry-run still performs no `POST` or `PATCH`.

Latest verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test .\AteraSnipeSync.sln
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 21 passed, 0 failed, 0 skipped.
Full solution tests passed: 136 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Previous Update

Snipe-IT preview/sync now creates missing companies and asset categories before hardware writes, and manual runs now save local reports/logs.

Code changed:

- Changed `SnipeImporter` so missing Snipe-IT asset categories are planned and created with `category_type = asset` instead of blocking assets when the operator-provided category, default `Computer`, is absent.
- Split real Snipe-IT writes into a reference phase and an asset phase: companies are created first, then categories, then models, and only after all reference writes succeed are hardware assets created or updated.
- Added `CreatedCategories` to `SnipeImportResult`, status history summaries, TrayApp summaries, and Snipe-IT preflight reporting.
- Added `snipeit-categories-plan.csv` to manual preflight CSV output. Missing-category previews show `Add,<category>,asset`; model rows leave `CategoryId` empty until the real category create returns an id.
- Defaulted the manual window's `Create Missing Companies` checkbox to checked for new local configurations while preserving saved operator choices.
- Connected the no-service `ManualSyncForm` to `JsonFileSyncStatusStore` so completed preview/sync runs save `SyncResult_*.json` under `C:\ProgramData\AteraSnipeSync\History`.
- Added date-split sanitized manual logs under `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`; each write recreates the daily file if it was deleted.
- Added `LocalAppSettingsStore.GetDefaultLogDirectory()` for the shared ProgramData log folder.
- Kept automated tests on mocked HTTP handlers only; no real Atera or Snipe-IT API call was added.

Tests changed:

- Replaced the old missing-category failure expectation with missing-category creation coverage.
- Added a test proving company/category/model POSTs occur before the first hardware POST.
- Added coverage for the new category preflight CSV and nullable category id in model preview rows.
- Added status history coverage for category create summaries and category change lists.
- Added local settings coverage for the default ProgramData log directory.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/technical-specs/05-StatusStore-技术规格.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/test-guides/05-StatusStore-单元测试指导手册.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI was consulted before changing Snipe-IT company/category/model/hardware behavior.
- Company creation remains controlled by `CreateMissingCompanies`; the manual UI now starts with that option checked for new configurations.
- Category creation has no separate toggle in this pass. The module uses the operator-provided category name and creates it as an asset category when missing.
- Manufacturer remains lookup-only; missing manufacturers still warn and allow model creation without `manufacturer_id`.
- Manual preflight CSV remains separate from final status/report history. Preview writes CSV review files and also saves a dry-run `SyncResult_*.json` history record.
- The no-service manual window now saves run reports directly. WorkerService production DI/scheduler wiring is still not implemented.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 133 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Previous Update

TrayApp manual sync window now exposes an explicit `Load Config` button.

Code changed:

- Added `Load Config` to `ManualSyncForm` action buttons.
- Clicking `Load Config` reloads reusable manual sync settings from the shared local settings store and applies them to the current form.
- The button is disabled while a connection test, preview, or sync run is active.
- Missing config is reported with a sanitized log message; loaded secrets are never printed.

Documentation changed:

- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Startup auto-load behavior remains unchanged; `Load Config` gives the operator a manual reload after copying or editing `appsettings.local.json`.
- The button reads `C:\ProgramData\AteraSnipeSync\appsettings.local.json` through `LocalAppSettingsStore.GetDefaultFilePath()` in the default app path.
- No Atera or Snipe-IT API integration code, DTOs, wire shapes, auth headers, or pagination behavior were changed.
- No automated UI click test was added; existing local settings tests cover the underlying read/write behavior, and the test guide now documents the manual smoke check.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 130 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp Load Config should receive a manual UI smoke check in the packaged build on the target server.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.

## Earlier Update

Snipe-IT preview planning now builds shared reference plans before matching assets.

Code changed:

- Changed `SnipeImporter` to validate records first, then plan unique company, category, manufacturer, and model references, then scan assets for MAC/serial/name matches.
- Reference-plan failures now block affected assets before `GET /hardware` asset lookups and are still written to `snipeit-assets-plan.csv`.
- Kept asset match lookups per asset, because MAC/serial/name matching is asset-specific.
- Updated progress messages so preview shows reference planning stages and the later asset matching pass.
- Updated `ImportAsync_WritesBlockedAssetRowsToPreflightCsv_WhenPlanningFails` to verify blocked reference-plan records do not query hardware.

Documentation changed:

- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- There is still no intentional `Task.Delay` in the Snipe-IT preview path.
- This does not invent a Snipe-IT batch endpoint or send 50 concurrent requests at once.
- Repeated reference names now cost one Snipe-IT lookup per unique reference value before the asset pass starts.
- Asset pass no longer contains company/category/manufacturer/model missing-reference decision logic; it applies the already-built reference plan.
- Snipe-IT API endpoint paths, payloads, auth headers, and pagination semantics were not changed.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 130 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.

## Current Focus

The current workstream has advanced through Snipe-IT Import manual preflight CSV, Worker Scheduler scheduling support, manual sync request shaping, and Sync Orchestrator pipeline execution.

Module 6 Notification now has the first-version safe stub implementation, tests, and test guide.

Snipe-IT asset matching priority remains:

1. MAC address exact match
2. Serial number exact match
3. Asset name high-similarity match

Assets created or updated by the importer include:

```text
Auto Synced from Atera at {UTC timestamp}
```

Manual sync can generate preflight CSV review files before real writes. Scheduler-triggered sync is unattended and must not generate manual preflight CSV files.

Manual UI semantics are now split:

- `Sync Now` directly runs the real sync pipeline and does not generate CSV
- `Preview Changes` generates temporary CSV only; confirm reruns the real sync pipeline and does not execute from the first CSV snapshot

Sync Orchestrator now provides the shared run boundary that manual, scheduled, worker, and future status/report flows should call.

## Important Safety Rules

Automated tests must never call the real Atera or Snipe-IT APIs.

All Atera and Snipe-IT API tests must use mocked HTTP handlers, fake clients, local fixtures, or sanitized hand-written payloads.

Do not add Python probes for real API verification.

Any real-key validation must be manual-only and owner/operator-run. Manual instructions must include exact commands or UI steps, local-only configuration, expected sanitized output, and cleanup steps. Real API keys must not be printed, logged, committed, or stored in tracked files.

## Documents Completed

### Atera Pull

The Atera Pull module plan/spec/test guide define:

- agent/device as the primary resource
- no standalone customer pull
- broad `AgentInfo` preservation for Atera `AgentQueryDTO` fields
- endpoint `GET /api/v3/agents`
- auth header `X-API-KEY`
- pagination with `page` and `itemsInPage`
- mocked HTTP tests only
- manual real API validation rules

### Reconstruction

The Reconstruction technical spec and tests define:

- `InventoryMapper` converts `AteraPullResult` into `SnipeImportBatch`
- missing company/manufacturer/model fallback behavior
- asset tag fallback behavior
- notes construction
- mapping warnings
- MAC addresses are passed from `AgentInfo.MacAddresses` into `SnipeAssetImportRecord.MacAddresses`

### Snipe-IT Import

The Snipe Import module plan/spec/test guide now define:

- official Snipe-IT OpenAPI sources checked before implementation
- Snipe-IT base URL format: `{SnipeItHost}/api/v1`
- bearer token authentication
- company ensure/create
- category lookup/create for missing asset categories
- manufacturer lookup only
- model ensure/create
- hardware match by MAC, serial, then name similarity
- hardware create/update
- dry-run write suppression
- HTTP 200 with `status = error` handling
- automatic asset notes line: `Auto Synced from Atera at {UTC timestamp}`
- manual sync preflight CSV files in `C:\ProgramData\AteraSnipeSync\Preflight\{runId}\`
- `snipeit-assets-plan.csv`, `snipeit-companies-plan.csv`, and `snipeit-models-plan.csv`
- CSV write failure blocks all Snipe-IT `POST` / `PATCH`

### Worker Scheduler

The Worker Scheduler module plan/spec/test guide now define:

- daily, weekly, and monthly schedules
- multiple local run times
- configured time zone
- monthly `DaysOfMonth` and `RunOnLastDayOfMonth`
- missing monthly dates are skipped, such as the 31st in February
- overlap prevention
- scheduler-triggered requests set `TriggeredBy = "scheduled"`
- scheduler-triggered requests disable manual preflight CSV

### Sync Orchestrator

The Sync Orchestrator module plan/spec/test guide now define:

- full pipeline order: Atera Pull, Reconstruction, then Snipe Import
- stage short-circuit rules
- warning aggregation rules
- import failure promotion to run-level failures
- dry-run propagation from `SyncRunOptions` to `SnipeImportOptions`
- cancellation behavior
- fake-dependency-only automated tests

### Status Store

The Status Store module plan/spec/test guide now define:

- all-history local persistence through `ISyncStatusStore`
- default history directory `C:\ProgramData\AteraSnipeSync\History`
- per-run file naming with UTC finished timestamps
- `SyncRunResult` to structured `SyncHistoryDocument` mapping
- structured resource change sets for assets, companies, models, manufacturers, and categories
- latest snapshot reconstruction by scanning history files
- latest success time reconstruction from history
- missing, empty, malformed, unsupported, and unreadable history behavior
- temp-file atomic write requirements
- no secrets, raw API payloads, full asset dumps, or dense string blobs in history JSON
- required `JsonFileSyncStatusStore` tests and latest verification result

### Notification

The Notification module plan/spec/test guide now define and verify:

- module boundary for notification request construction, event filtering, and publisher substitution
- first-version no-external-send behavior through `NullNotificationPublisher`
- stable event names for scheduled, manual, manual-preview, and generic sync notifications
- severity mapping for information, warning, error, and critical notifications
- `NotificationConfig.Enabled` and `NotificationConfig.OnEvents` filtering semantics
- safe subject/message content rules that exclude secrets and raw payloads
- implemented classes: `NotificationEventTypes`, `NotificationRequestFactory`, `NotificationEventFilter`, and `NullNotificationPublisher`
- implemented unit tests and offline-only testing rules

### TrayApp

The TrayApp module plan/spec/test guide define:

- Atera API key input through a password field
- local save path `C:\ProgramData\AteraSnipeSync\appsettings.local.json`
- schedule editor requirements for daily / weekly / monthly
- local schedule config save/load under `Sync.Schedule`
- manual sync preflight CSV confirmation UI requirements
- manual `Sync Now` and `Preview Changes` are separate actions
- scheduler automatic sync must not show manual confirmation UI
- no real API calls from the settings UI
- mocked/temporary-file-only automated tests

## Code Completed

### Core Atera

Implemented:

- `AteraClient`
- `AgentInfo`
- `AteraPullException`
- `AteraPullFailureKind`
- `AteraPullOptions`
- `IAteraClock`
- `SystemAteraClock`
- `AteraWarningFactory`

`AteraClient` now:

- calls `GET /api/v3/agents`
- sends `X-API-KEY`
- requests `page` and `itemsInPage=500`
- parses the response envelope
- maps valid records to `AgentInfo`
- preserves broad Atera device fields including MAC/IP arrays and raw JSON
- skips malformed single records with warnings
- retries transient failures
- fails without returning partial results after retry exhaustion or malformed pagination state
- avoids logging API keys

### Core Reconstruction

Implemented:

- `InventoryMapper`
- `AssetTagFactory`
- `MappingValueResolver`
- `NotesBuilder`
- `MappingWarningFactory`

`InventoryMapper` now maps:

- serial
- asset tag
- name
- company/manufacturer/model/category/status
- notes
- source identity
- MAC address list

### Core Snipe-IT Import

Implemented:

- `SnipeImporter`
- `SnipeImportPreflightCsvWriter`
- `SnipeImportPreflightPlan`
- `MacAddressNormalizer`
- `AssetNameMatcher`
- `SnipeApiException`
- `SnipeAssetMatch`

Updated contracts:

- `SnipeAssetImportRecord.MacAddresses`
- `SnipeImportOptions.MacAddressCustomFieldDbColumnName`
- `SnipeImportOptions.NameMatchThreshold`
- `SnipeImportOptions.ManualPreflightCsvEnabled`
- `SnipeImportOptions.ManualPreflightCsvDirectory`

`SnipeImporter` supports:

- `GET /companies?name=...`
- `POST /companies`
- `GET /categories?search=...`
- `POST /categories`
- `GET /manufacturers?name=...`
- `GET /models?search=...&category_id=...`
- `POST /models`
- `GET /hardware?limit=50&filter=...`
- `GET /hardware/byserial/{serial}`
- `GET /hardware?limit=50&search=...`
- `POST /hardware`
- `PATCH /hardware/{id}`

Write behavior:

- plans shared company/category/manufacturer/model references before asset matching
- writes manual preflight CSV when enabled after planning and before real writes
- blocks assets with missing reference plans before hardware matching
- creates missing companies only when enabled and not dry-run
- creates missing asset categories when absent and not dry-run
- creates missing models only when enabled and not dry-run
- does not create manufacturers
- creates hardware when no reliable match exists
- updates hardware when a unique MAC, serial, or high-confidence name match exists
- fails ambiguous MAC/name matches instead of updating an arbitrary asset
- appends `Auto Synced from Atera at {UTC timestamp}` to asset notes on create/update

### Core Worker Scheduler

Implemented:

- `SyncScheduleOptions`
- `ScheduleFrequency`
- `ScheduleCalculator`
- `ScheduledSyncRequestFactory`
- `SyncScheduler`

Scheduler behavior:

- calculates next UTC run from configured local schedule
- supports daily, weekly, monthly, multiple run times, and configured time zone
- skips monthly dates that do not exist in a given month
- prevents overlapping scheduled runs when enabled
- runs scheduler requests with `TriggeredBy = "scheduled"`
- disables `ManualPreflightCsvEnabled` for scheduled requests

### Core Manual Sync Requests

Implemented:

- `ManualSyncRequestFactory`

Manual request behavior:

- `CreateSyncNowRequest` sets `TriggeredBy = "manual"`
- `CreateSyncNowRequest` disables dry-run and manual preflight CSV
- `CreatePreviewChangesRequest` sets `TriggeredBy = "manual-preview"`
- `CreatePreviewChangesRequest` enables dry-run and manual preflight CSV
- preview requests require a non-blank preflight directory

### Core Sync Orchestrator

Implemented:

- `SyncOrchestrator`

`SyncOrchestrator` now:

- calls `IAteraClient.PullInventoryAsync`
- calls `IInventoryMapper.Map`
- calls `ISnipeImporter.ImportAsync`
- preserves successful prior stage output when a later stage fails
- aggregates module warnings
- converts stage exceptions to `SyncFailure`
- converts `SnipeImportResult.Failures` to run-level `SyncFailure`
- applies `SyncRunOptions.DryRun` to importer options
- rethrows cancellation
- uses `TimeProvider` for deterministic timestamps in tests

### Core Status Store

Implemented:

- `JsonFileSyncStatusStore`
- `SyncStatusStoreOptions`
- `SyncHistoryDocument`
- `SyncHistoryRunInfo`
- `SyncHistorySummary`
- `SyncHistoryChangeSet`
- `SyncHistoryItem`
- `SyncHistoryWarning`
- `SyncHistoryFailure`

`JsonFileSyncStatusStore` now:

- writes every completed `SyncRunResult` to a separate structured history JSON file
- uses UTC `FinishedAt` timestamps in `SyncResult_yyyyMMdd_HHmmss_fffffffZ.json` file names
- appends a short GUID suffix when a history file name already exists
- writes through a temp file followed by a non-overwriting move
- maps import actions and import failures into structured resource change sets
- preserves empty `deleted` arrays under the current no-delete policy
- skips malformed or unsupported files during latest-status reads
- computes `LastSuccessAt` by scanning prior valid history files
- avoids persisting Atera raw JSON, mapped asset dumps, API keys, or tokens

### Core Notification

Implemented:

- `NotificationEventTypes`
- `NotificationRequestFactory`
- `NotificationEventFilter`
- `NullNotificationPublisher`

`NotificationRequestFactory` now:

- maps scheduled, manual, manual-preview, and unknown triggers to stable event names
- maps success, warning-only success, normal failure, and auth/credential failure to the required severities
- builds concise subjects for each event type
- builds safe summary messages with timestamps, counts, warning count, and first failure context
- redacts unsafe first-failure message text that appears to contain secrets or raw payloads

`NotificationEventFilter` now:

- suppresses all events when notifications are disabled
- suppresses all events when `OnEvents` is empty
- matches configured events after trim with ordinal ignore-case comparison
- ignores blank configured event names

`NullNotificationPublisher` now:

- validates required request fields
- honors cancellation before logging
- writes a debug log with event type, severity, and subject only
- sends no HTTP, SMTP, Teams, Slack, webhook, Atera, or Snipe-IT traffic

### TrayApp Local Settings

Implemented:

- `LocalAppSettingsStore`
- `SettingsForm`
- `ManualSyncForm`
- local JSON config preservation when saving Atera API key
- schedule config save/load under `Sync.Schedule`
- default manual preflight directory helper
- replacement of default `Form1` with a temporary no-service manual sync validation window
- direct manual validation path for `Preview Changes` and `Sync Now` through the existing Core orchestrator

### Interface Organization

Core interface files have been organized into module-local `Interfaces/` folders while keeping namespaces unchanged:

- `Atera/Interfaces/IAteraClient.cs`
- `Atera/Interfaces/IAteraClock.cs`
- `Mapping/Interfaces/IInventoryMapper.cs`
- `Notifications/Interfaces/INotificationPublisher.cs`
- `Scheduling/Interfaces/ISyncScheduler.cs`
- `SnipeIt/Interfaces/ISnipeImporter.cs`
- `Status/Interfaces/ISyncStatusStore.cs`
- `Sync/Interfaces/ISyncOrchestrator.cs`

## Tests Completed

Automated tests now cover:

- Atera official endpoint/auth/pagination shape
- Atera broad `AgentInfo` field preservation
- Atera retry/auth/malformed/pagination/no API-key logging behavior
- Reconstruction mapping and warnings
- MAC address transfer from Atera mapping into Snipe import records
- Snipe-IT company create
- Snipe-IT model create
- Snipe-IT missing asset category creation
- Snipe-IT MAC-first asset update
- Snipe-IT serial fallback update
- Snipe-IT high-similarity name fallback update
- Snipe-IT asset create when no match exists
- Snipe-IT dry-run suppresses POST/PATCH writes
- Snipe-IT ambiguous MAC match failure
- Snipe-IT HTTP 200 business error failure
- Snipe-IT automatic notes line on create/update payloads
- manual preflight CSV files are written before Snipe-IT mutations
- manual preflight CSV write failure blocks Snipe-IT mutations
- dry-run plus manual preflight writes CSV but suppresses mutations
- Snipe-IT reference-plan-blocked assets are written to CSV and do not query hardware
- repeated Snipe-IT reference names are planned once per unique reference before asset matching
- scheduler daily/weekly/monthly next-run calculation
- monthly 31st skips months without that date
- scheduler request factory disables manual preflight CSV
- scheduler overlap prevention
- sync orchestrator successful pipeline ordering
- sync orchestrator pull/map/import short-circuit behavior
- sync orchestrator warning aggregation
- sync orchestrator import failure promotion
- sync orchestrator dry-run option propagation
- sync orchestrator cancellation rethrow behavior
- manual sync request factory separates `Sync Now` from `Preview Changes`
- status store per-run history JSON persistence
- status store UTC file naming and conflict-safe file creation
- status store structured resource change sets
- status store malformed history skipping and latest snapshot reconstruction
- status store latest success timestamp scanning
- status store cancellation and constructor validation behavior
- notification request event mapping and subject/severity construction
- notification safe summary counts and first-failure context
- notification secret/raw-payload exclusion from generated messages
- notification event filtering for disabled, empty, unmatched, and case-insensitive configured events
- null notification publisher validation and cancellation behavior
- TrayApp/local settings read/write behavior
- schedule config save/load and invalid schedule rejection
- default manual preflight directory shape

## Not Implemented Yet

Still pending:

- WorkerService wiring for `AteraClient`, `InventoryMapper`, `SnipeImporter`, `SyncOrchestrator`, and `SyncScheduler`
- production DI registration
- encrypted secret storage
- TrayApp Snipe-IT configuration UI
- TrayApp schedule editor UI implementation
- formal TrayApp/WorkerService IPC for manual sync
- production TrayApp manual sync preflight confirmation UI implementation
- TrayApp status viewer
- installer/service deployment scripts
- concrete email, Teams, Slack, and webhook notification senders
- manufacturer creation policy
- automatic discovery of Snipe-IT MAC custom field db column

## Verification

Last successful commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Last known test result:

```text
Passed: 130
Failed: 0
Skipped: 0
```

## Current Workspace Notes

The working tree contains earlier uncommitted changes from Atera Pull, Reconstruction, TrayApp, Snipe-IT Import, and Scheduler work. Do not revert unrelated modified or deleted files unless explicitly requested.

Notable current implementation files:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightCsvWriter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightPlan.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduleCalculator.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduledSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Scheduling/SyncScheduler.cs`
- `src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs`
- `src/AteraSnipeSync.Core/Sync/ManualSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Status/SyncStatusStoreOptions.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationRequestFactory.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationEventFilter.cs`
- `src/AteraSnipeSync.Core/Notifications/NullNotificationPublisher.cs`
- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`
- `src/AteraSnipeSync.TrayApp/Program.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduleCalculatorTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/SyncSchedulerTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/ManualSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NotificationRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NotificationEventFilterTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NullNotificationPublisherTests.cs`
- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`

`AGENTS.md` now includes the progress documentation requirement: every code/test change must update `progress.md` in the same work session.

## Recommended Next Step

Wire WorkerService/DI so the runtime calls `SyncOrchestrator`, saves the resulting `SyncRunResult` through `JsonFileSyncStatusStore`, and publishes filtered notification requests through the completed notification module.

## 2026-07-15 Codebase Hardening Checkpoint (Paused)

The user requested a pause after adopting a test-stage secret boundary. Current credential behavior:

- `appsettings.local.json` no longer saves `Atera.ApiKey` or `SnipeIt.ApiToken`.
- runtime secrets are read from process/service environment variables `ATERA_API_KEY` and `SNIPEIT_API_TOKEN`.
- WorkerService depends on the read-only `ILocalAppSettingsReader`; it never writes configuration.
- WorkerService rejects legacy plaintext secret fields without modifying the file.
- TrayApp explicit config save removes legacy plaintext fields and preserves non-secret settings.

Implemented in this hardening pass:

- strict Snipe-IT `rows` envelope and row validation, safe pagination offsets, create-id validation, GET-only retry, mutation audit correctness, partial-cancellation result, batch/target identity conflict gates, indexed name fallback, custom-field validation, and CSV formula neutralization
- Atera HTTPS/official-host validation, configurable bounded probe page size/count, `Retry-After` plus exponential retry delay
- warning de-duplication, stable Atera failure codes, correct scheduled notification event/filter behavior, and scheduler exception survival
- atomic/cross-process serialized local settings writes, atomic status writes, history retention options, and asynchronous TrayApp daily logging
- WorkerService composition root and Windows Service hosting instead of the template one-second logger loop
- dependency patch updates, NuGet lock files, `.editorconfig`, `Directory.Build.props`, `global.json`, and Windows CI workflow

Verification completed at the pause point:

```powershell
dotnet build AteraSnipeSync.sln -c Release --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj -c Release --no-build
```

Latest result:

```text
Build: succeeded, 0 warnings, 0 errors
Tests: 153 passed, 0 failed, 0 skipped
```

Remaining work is intentionally paused pending user prioritization. Use functional-test-stage standards rather than full production hardening for the next pass.

## 2026-07-15 Manual Sync Local Credential Persistence

At the user's request, the Manual Sync functional-test flow now persists credentials locally so they do not need to be re-entered on every TrayApp launch:

- `Save Config` writes trimmed `Atera.ApiKey` and `SnipeIt.ApiToken` to `C:\ProgramData\AteraSnipeSync\appsettings.local.json` through the existing atomic/cross-process serialized settings writer.
- TrayApp startup and `Load Config` read both credentials from that local file and repopulate the masked fields.
- Manual Sync no longer uses process environment variables as credential overrides.
- UI status/log text now states that credentials were saved to the sensitive local plaintext test file.
- The sample Snipe-IT base URL now includes the required `/api/v1` path.
- WorkerService behavior was intentionally not changed; it still rejects plaintext credentials and expects environment variables. Worker reconciliation remains future work after Manual Sync testing.

Documentation changed:

- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `samples/configs/appsettings.local.example.json`

Verification command:

```powershell
dotnet test AteraSnipeSync.sln --configuration Release --no-restore
dotnet build AteraSnipeSync.sln --configuration Release --no-restore
git diff --check
```

Latest result:

```text
Build: succeeded, 0 warnings, 0 errors
Tests: 156 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

## 2026-07-15 Run-Local Snipe-IT Company Snapshot

Company reference validation now avoids one API request per unique company:

- consulted the official Snipe-IT `/companies` reference and OpenAPI response contract before changing integration behavior
- each non-empty import run calls `GET /companies` once without a company-name query
- all returned `{ id, name }` rows are loaded into a normalized in-memory company index shared by every source record in that run
- `total` must equal `rows.Count`; incomplete company snapshots fail with `SnipeImport.IncompleteCompanySnapshot` instead of being misclassified as missing companies
- duplicate normalized company names with different ids fail with `SnipeImport.AmbiguousCompanyMatch`
- missing-company planning and optional `POST /companies` creation behavior are unchanged
- company snapshot request/response failures are stored once and applied to all affected company references; there is no fallback to per-company GET requests

Production code and tests changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --configuration Release --no-restore
dotnet build AteraSnipeSync.sln --configuration Release --no-restore
```

Latest result:

```text
SnipeImporter tests: 41 passed, 0 failed, 0 skipped
All tests: 158 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
```

## 2026-07-15 Configurable Ignored MAC Addresses

Manual, preview, real, and scheduled runs now share a persisted ignored-MAC configuration:

- `SnipeIt.IgnoredMacAddresses` is loaded and saved as a normalized, de-duplicated JSON string array.
- Manual Sync UI adds `Ignore MAC Addresses (; separated)` and reloads saved values.
- ignored MACs remain visible in mapped records and preflight CSV for audit, but no longer create mapping duplicate warnings or `SnipeImport.DuplicateBatchIdentity` blocks.
- source and target MAC matching excludes ignored values, and create/update payloads select the first valid non-ignored MAC for the configured custom field.
- invalid configured MACs fail before Snipe-IT lookup or mutation.
- request copies in Manual Sync, Sync Orchestrator, scheduler, and WorkerRuntimeFactory preserve the same ignored list.

Production code and tests changed across Configuration, Mapping, SnipeImport, TrayApp, WorkerService, manual/scheduled request factories, and their unit tests.

Documentation changed:

- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/module-plans/04-SyncOrchestrator-功能职责.md`
- `docs/module-plans/07-WorkerScheduler-功能职责.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- corresponding technical specs and unit test guides
- `samples/configs/appsettings.local.example.json`

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore
dotnet build .\AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Tests: 167 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: enter `00:09:0F:AA:00:01; 00:09:0F:FE:00:01` in the new field, save/reload config, then run Preview Changes against the operator's real local environment. Real API verification remains manual-only.

## 2026-07-15 Virtual Machine Shared-Serial Identity

Repeated Atera serials no longer block distinct records whose normalized model is exactly `Virtual Machine`:

- Reconstruction detects duplicate normalized serial groups and marks only their VM serials as audit-only; Asset Tags now follow the later global `ATERA-{SourceId}` rule for every mapped asset.
- the original serial remains on the import record and Preview CSV for audit, with `SerialIsReliableIdentity = false`.
- audit-only serials do not participate in mapping/import duplicate gates, target serial matching, target serial mismatch checks, or Snipe-IT create/update payloads.
- Snipe hardware snapshots now include an exact, case-insensitive asset-tag index; asset tag, MAC, and reliable serial matches must resolve to the same target id.
- ambiguous asset tags use `SnipeImport.AmbiguousAssetTagMatch`; conflicting strong identities remain blocked with `SnipeImport.ConflictingStrongIdentityMatch`.
- physical-machine duplicate serial behavior is unchanged.

Official Snipe-IT references consulted before changing integration behavior:

- hardware list (`asset_tag` and `serial` response fields)
- hardware create/update (`asset_tag` required and unique; `serial` optional)
- asset tag documentation (no two assets may share an asset tag)

Production code and tests changed:

- `src/AteraSnipeSync.Core/Mapping/InventoryMapper.cs`
- `src/AteraSnipeSync.Core/Mapping/MappingWarningFactory.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeAssetImportRecord.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted tests: 66 passed, 0 failed, 0 skipped
All tests: 173 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

## 2026-07-15 Uniform Atera Source Asset Tags

All Reconstruction output now identifies Atera ownership directly in the Snipe-IT Asset Tag:

- every mapped record uses `AssetTag = ATERA-{SourceId}`, regardless of whether a serial is present
- `SourceId` remains the normalized Atera Agent ID when available; a valid serial is retained only as the compatibility fallback when Agent ID is missing
- serial remains an independent audit/matching field and no longer determines Asset Tag
- physical records with the same reliable serial still trigger `DuplicateSerial`, while different SourceIds no longer create an incidental duplicate Asset Tag
- the Virtual Machine shared-serial exception now only changes serial reliability; its Asset Tag already follows the global rule
- legacy Snipe-IT assets whose Asset Tag was the serial can be found through reliable serial matching and PATCHed to the new `ATERA-{SourceId}` value

The official Snipe-IT hardware list/update documentation was rechecked before adding the migration payload test: hardware snapshots return `asset_tag` and `serial`, and update requires a unique `asset_tag` while accepting `serial` separately.

Production code and tests changed:

- `src/AteraSnipeSync.Core/Mapping/AssetTagFactory.cs`
- `src/AteraSnipeSync.Core/Mapping/InventoryMapper.cs`
- `tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs`
- `tests/AteraSnipeSync.Tests/Mapping/ReconstructionBoundaryTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/AteraSnipeSync_AI_Agent_Master_Plan.md`
- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~ReconstructionBoundaryTests|FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted tests: 69 passed, 0 failed, 0 skipped
All tests: 175 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: run Preview Changes and confirm every planned Asset Tag begins with `ATERA-`; then review any legacy-tag rows planned as Modify before real Sync. Real API verification remains manual-only.
## 2026-07-15 Laptop/Desktop Model Category Normalization

Superseded on 2026-07-16 by the complete `/models` normalization described in Latest Update; the historical details below record the earlier limited design.

TrayApp now provides an operator-confirmed maintenance button for consolidating historical Snipe-IT Model categories:

- `Normalize Categories` reads the current Snipe-IT URL/token and program `Default Category` (default `Computer`).
- Core paginates the complete hardware snapshot, selects only actually referenced Models whose current category is exactly Laptop or Desktop (case-insensitive), deduplicates by Model ID, and counts affected assets.
- the existing target category is resolved from the complete category snapshot; missing, ambiguous, non-asset, inconsistent, or malformed snapshots stop before any write.
- after the UI confirmation, each distinct Model is updated once through documented `PUT /models/{id}` with the required `name` and `category_id`; no Asset/Model/Category is created or deleted.
- independent Model failures are classified and logged while later Models continue; cancellation is checked before the next PUT and a PUT already sent waits for a definite response.
- every click produces a dedicated `ModelCategoryNormalization_yyyyMMdd_HHmmss_fffffff.log` with scan/plan/outcome/summary details. If that audit log cannot be created during planning, mutation is blocked.

Official Snipe-IT documentation/source checked before implementation:

- hardware list `GET /hardware` and category list `GET /categories` pagination
- model update `PUT /models/{id}` requiring `name` and `category_id`
- official `AssetModelsController.update`, confirming `fill($request->all())` changes only submitted fields so optional Model fields are preserved when omitted

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeModelCategoryNormalization.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeModelCategoryNormalizer.cs`
- `src/AteraSnipeSync.TrayApp/ModelCategoryNormalizationLog.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeModelCategoryNormalizerTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeModelCategoryNormalizerTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted normalization tests: 11 passed, 0 failed, 0 skipped
All tests: 186 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: back up Snipe-IT, run the new button, review the confirmation counts, then verify the dedicated log and a sample of updated Models. No automated test calls the real Snipe-IT API.
## 2026-07-15 Accurate Real-Sync Reference Progress

The real Snipe-IT reference creation phase no longer displays the mapped asset count as a misleading reference denominator:

- planned Company, Category, and Model creates are materialized and counted before mutations
- the initial progress line reports the real total plus per-type counts
- every reference POST reports its 1-based position, type, safe name, success duration, or failure code/duration
- zero missing references reports an explicit no-op
- Company -> Category -> Model ordering, endpoints, payloads, non-replay mutation behavior, and cancellation semantics are unchanged
- TrayApp bypasses normal heartbeat/milestone throttling for these controlled reference progress messages, so every creating/created/failed line is retained in the UI and daily Manual Sync log

Official Snipe-IT documentation rechecked before the change:

- API overview, including HTTP 200 bodies with `status=error` and structured `messages`
- Company create `POST /companies`
- Category create `POST /categories`
- Model create `POST /models`

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
  - all-missing reference case asserts total=3 and Company/Category/Model positions 1/2/3 with durations
  - shared failing Company case asserts reference total=1 rather than two dependent assets and retains the validation code
  - no-missing reference case asserts an explicit total=0 no-op

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted SnipeImporter tests: 52 passed, 0 failed, 0 skipped
All tests: 187 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: run a real Sync Now containing missing references and confirm the UI/daily log shows the real reference total plus every per-reference duration. Automated tests still never call the real Snipe-IT API.

## 2026-07-16 Unified Default-Category Model MAC Fieldset Preparation

Preview Changes, Sync Now, and scheduled sync now share one upfront Model preparation phase:

- the GUI has an editable `MAC Fieldset Name`; the name is operator configuration rather than a hardcoded Snipe-IT default
- MAC custom-field DB column and Fieldset name are persisted and validated as a pair
- the complete `GET /models` snapshot is inspected once before per-asset matching, and `GET /fieldsets` is resolved once when MAC Fieldset reconciliation is enabled
- MAC Fieldset reconciliation applies only to all Models already in the configured `Default Category`, plus Models being normalized from the configured source category list into that target; other categories are untouched
- runs containing only other-category Models skip Fieldset resolution entirely
- new Models created in the default category include `fieldset_id`
- existing Models missing the target Fieldset are planned as Model `Modify`; category normalization and Fieldset assignment are combined into one `PATCH /models/{id}`
- Model creates/updates execute before any Asset create/update
- Preview writes the same Add/Modify Model plan without mutations and reports `UpdatedModels`
- the standalone Normalize Categories UI button was removed from the normal action panel so category behavior does not diverge from Preview/Sync

Official Snipe-IT documentation/source checked before implementation:

- `GET /fieldsets`
- `POST /models` with `fieldset_id`
- `PATCH /models/{id}` with partial `category_id` / `fieldset_id`
- Model response transformer output containing nested `fieldset.id/name`

Production code changed:

- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`
- `src/AteraSnipeSync.Core/Configuration/ManualSyncSettings.cs`
- `src/AteraSnipeSync.Core/Configuration/SnipeItConfig.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportOptions.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightCsvWriter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightPlan.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportResult.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Sync/ManualSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduledSyncRequestFactory.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`
- `src/AteraSnipeSync.WorkerService/Worker.cs`
- `src/AteraSnipeSync.WorkerService/WorkerRuntimeFactory.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`
- `tests/AteraSnipeSync.Tests/ContractCompilationTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
- `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/ManualSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `docs/test-guides/04-SyncOrchestrator-单元测试指导手册.md`
- `docs/test-guides/07-WorkerScheduler-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test .\AteraSnipeSync.sln --no-restore /nr:false
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
git diff --check
```

Latest known result:

```text
All tests: 204 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: enter the actual Snipe-IT Fieldset name in the GUI, run Preview Changes, confirm only default-category/normalizing Models appear as Modify, then run Sync Now and verify Model mutations complete before Asset writes. Automated tests use mocked HTTP only and never call the real Snipe-IT API.

## 2026-07-16 Unique Model Number Reuse With Manufacturer/Category Guard

Model resolution now safely reuses an existing Snipe-IT Model when the Atera model name does not match an existing Model name:

- exact normalized Model name remains the first-priority lookup
- when name has no match, an exact `model_number` match is considered
- reuse is allowed only when exactly one Model id has that model number, its `manufacturer.id` equals the resolved source manufacturer id, and its current `category.id` equals the requested category id
- multiple Model ids with the same model number remain blocked even if one candidate has matching manufacturer/category
- missing or mismatched manufacturer/category remains `SnipeImport.ModelNameConflict`
- successful fallback reuse supplies the existing Model id to the normal Asset plan and does not create a Model
- the complete existing `GET /models` snapshot supplies model number, nested manufacturer, and nested category data; no per-asset or per-model API request was added

Official Snipe-IT documentation/source checked before implementation:

- current `/models` API documentation for the paginated Model list and `model_number`
- official `AssetModelsTransformer`, confirming nested `manufacturer { id, name }` and `category { id, name }` in Model rows

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
  - unique model number + manufacturer/category match reuses the existing Model id
  - manufacturer mismatch blocks
  - category mismatch blocks
  - multiple model-number matches block without selecting the otherwise matching candidate

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests" /nr:false
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
Targeted SnipeImporter tests: 66 passed, 0 failed, 0 skipped
All tests: 207 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: rerun Preview Changes against the same environment. The fifteen unique model-number conflicts should now plan normally; the duplicate `PowerEdge R640` model-number match must remain blocked. Automated tests use mocked HTTP only and never call the real Snipe-IT API.

## 2026-07-16 Manufacturer Alias Mapping for Shared Preview/Sync

Implemented deterministic manufacturer aliases by reusing the existing company-alias pattern instead of fuzzy auto-matching.

Production code changed:

- `MappingOptions` and `ManualSyncSettings` now expose `ManufacturerAliases` with an empty-dictionary default.
- `MappingValueResolver` now applies one-way manufacturer aliases after resolving the Atera manufacturer or configured fallback. Alias-key comparison trims, ignores case, collapses whitespace, treats NBSP as whitespace, and normalizes common dash-like characters; no similarity score can trigger automatic reuse.
- `LocalAppSettingsStore` loads/saves `Mapping.ManufacturerAliases` for manual and Worker runs.
- `ManualSyncForm` adds a `Manufacturer Aliases` multiline field using `Atera manufacturer=Snipe-IT manufacturer`, persists it, and passes it into the shared `MappingOptions` used by Preview Changes and Sync Now.
- `WorkerRuntimeFactory` passes the same aliases into unattended mapping.
- the sample local configuration includes `Dell Inc.=Dell`.
- unique model-number fallback now treats a matching planned category-normalization update as the Model's effective category. This includes a default category being created in the same run, provided the Model update and record share the same planned category object.

Tests changed:

- mapper tests cover `Dell Inc. -> Dell` and equivalent whitespace/dash alias keys.
- local settings tests cover manufacturer alias JSON save/load round-trip.
- a mocked Preview integration test maps `Dell Inc.` to `Dell` and verifies canonical manufacturer lookup plus unique model-number reuse without a Model Add.
- a mocked Preview test verifies unique model-number reuse when the existing Model is planned from `Laptop` to a same-run-created `Computer` category; unplanned category mismatch and multiple model-number tests remain fail-closed.

Documentation changed:

- module plans: Reconstruction, WorkerScheduler, TrayApp
- technical specs: Reconstruction, WorkerScheduler, TrayApp
- test guides: Reconstruction, SnipeImport, WorkerScheduler, TrayApp

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~InventoryMapperTests.Map_UsesManufacturerAlias|FullyQualifiedName~LocalAppSettingsStoreTests.SaveManualSyncSettingsAsync_SavesManualPanelConfig|FullyQualifiedName~LocalAppSettingsStoreTests.LoadManualSyncSettingsAsync_ReturnsSavedManualPanelConfig|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse"
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests.ImportAsync_ReusesUniqueModelNumber_WhenCategoryWillBeNormalizedToPlannedCategory|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse|FullyQualifiedName~SnipeImporterTests.ImportAsync_BlocksUniqueModelNumber_WhenCategoryDoesNotMatch|FullyQualifiedName~SnipeImporterTests.ImportAsync_BlocksModelNumber_WhenMultipleModelsMatch"
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused manufacturer alias tests: 5 passed, 0 failed, 0 skipped
Focused effective-category/model-number tests: 4 passed, 0 failed, 0 skipped
All tests: 211 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
```

Remaining manual-only verification: enter `Dell Inc.=Dell` in the new GUI field, save/reload, and rerun Preview against the rolled-back test database. Manufacturer aliasing removes the unresolved `Dell Inc.` guard, and a matching category-normalization plan now satisfies the category guard even when the default category is created in the same run. Multiple model-number matches remain intentionally blocked. Automated tests use mocked HTTP only and never call real APIs.

## 2026-07-17 Company and Manufacturer Direct-Match-First Alias Resolution

Corrected alias precedence so an operator alias is a fallback, not an unconditional replacement.

Production code changed:

- `SnipeAssetImportRecord` now carries `CompanyAliasName` and `ManufacturerAliasName` separately from the preserved source names.
- `MappingValueResolver` and `InventoryMapper` retain the Atera/default company and manufacturer names while producing optional deterministic alias candidates.
- company planning checks the complete in-memory Snipe company snapshot first; a direct source-name match is retained and the alias is considered only when the source is absent.
- manufacturer planning performs one exact source lookup per unique source manufacturer, then performs the alias lookup only after a not-found result. The selected effective name/id is applied once before Model planning, so Preview and Sync Now share the same decision and do not repeat it per asset.
- if both manufacturer lookups are missing, the existing lookup-only behavior remains: Model planning may continue without a manufacturer binding and exactly one `SnipeImport.ManufacturerMissing` warning names the final alias candidate.
- no API endpoint, request payload field, response DTO, authentication, or pagination shape changed in this correction.

Tests changed:

- mapper tests verify source names remain in `CompanyName` / `ManufacturerName` and aliases are carried separately.
- company Preview tests cover direct-source precedence, alias-target reuse, and alias-target create planning.
- manufacturer Preview tests cover direct-source precedence with one lookup, source-miss alias fallback, both-missing single-warning behavior, and existing unique model-number reuse.
- multiple model-number matches remain fail-closed and are not narrowed by aliases.

Documentation changed:

- module plans and technical specs for Reconstruction and Snipe Import.
- unit test guides for Reconstruction and Snipe Import.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName|FullyQualifiedName~ImportAsync_PreviewPrefersDirectCompanyMatchOverConfiguredAlias|FullyQualifiedName~ImportAsync_PreviewCreatesAliasTarget_WhenSourceAndAliasAreMissing|FullyQualifiedName~ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse|FullyQualifiedName~ImportAsync_PreviewPrefersDirectManufacturerMatchOverConfiguredAlias"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~SnipeImporterTests"
dotnet build tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --no-incremental
dotnet test AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused alias tests: 26 passed, 0 failed, 0 skipped
SnipeImporter tests before the final single-warning addition: 71 passed, 0 failed, 0 skipped
All tests including the final addition: 225 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: rerun Preview Changes against the rolled-back test database with an alias whose source name also exists in Snipe-IT. The Preview CSV must retain the source company/manufacturer and must not plan the alias target. Then test a missing source name to confirm the alias fallback still resolves. Automated tests use mocked HTTP only and never call real APIs.

## 2026-07-21 AntdUI Dashboard Refresh and File-Only Detailed Logs

Modernized both TrayApp windows with AntdUI 2.4.3 and removed the main Dashboard log text area.

Production code changed:

- added the AntdUI 2.4.3 package using its native `net10.0-windows7.0` asset and enabled the light theme with application-wide Segoe UI typography.
- `TrayDashboardForm` and `SyncConfigurationForm` now inherit `AntdUI.Window` and use AntdUI page headers, panels, buttons, labels, progress, tabs, inputs, select, and checkbox controls.
- added `TrayUiTheme` for the shared canvas/surface palette, status colors, AntdUI card shadows/radii, and primary/default/error button hierarchy.
- replaced the Dashboard multiline result/log text box with System Status, Automation, Current Activity, and Latest Run cards plus six Pulled/Mapped/Created/Updated/Skipped/Failed metrics.
- normalized `SyncProgressCalculator` percentages into AntdUI `Progress.Value`'s `0F..1F` contract.
- `TrayApplicationContext` now owns and flushes one `DailyLogWriter`; each Tray-started command writes start, every detailed progress callback, connection results, terminal summary, and structured failures to `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`.
- the Dashboard no longer renders per-record progress or grouped failure details; it shows only stable phase/status text and compact count summaries.

Documentation changed:

- updated `docs/module-plans/08-TrayApp-功能职责.md` with the AntdUI visual boundary and file-only detailed-log policy.
- updated `docs/technical-specs/08-TrayApp-技术规格.md` with exact AntdUI classes, package version, progress normalization, constructor dependency, and logging ownership/data flow.
- updated `docs/test-guides/08-TrayApp-单元测试指导手册.md` with the new production file, automated boundary, and manual visual/log acceptance steps.

Verification commands:

```powershell
dotnet restore src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TrayApp"
dotnet test AteraSnipeSync.sln --no-build --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest known result:

```text
NuGet restore: succeeded
TrayApp build: succeeded, 0 warnings, 0 errors
TrayApp tests: 34 passed, 0 failed, 0 skipped
All tests: 275 passed, 0 failed, 0 skipped
Solution build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: open the tray Dashboard on the operator workstation and confirm AntdUI shadow/animation, DPI scaling, PageHeader window controls, compact metrics at the minimum size, and daily log flush after Exit Tray App. This change does not alter Atera/Snipe-IT endpoints, DTOs, payloads, authentication, or pagination, and automated tests did not call real APIs.

## 2026-07-21 Tray Action Consolidation and Configuration Connection Test

Reduced the Dashboard action count and moved connection testing into the API configuration workflow.

Production code changed:

- removed Test Connections, Re-register & Restart, Open Preflight, Open History, and Open Logs from the Dashboard.
- the Dashboard now exposes Config, Preview, Sync Now, Cancel, one fixed-label Restart Service, and one Open Log Folder action.
- Restart Service never changes its label to Start Service; when the Service is stopped, the existing restart helper starts it. The internal fixed-identity re-register helper remains for compatibility but is no longer UI-reachable.
- added Test Connections to `API & Credentials`. It validates and atomically saves the complete current form, sends payload-free `ReloadSchedule`, then sends payload-free `TestConnections` through Worker IPC.
- the configuration page shows only bounded Atera/Snipe-IT success/failure status; detailed progress and sanitized endpoint messages continue to `ManualSync_yyyyMMdd.log`.
- the Dashboard and tray context menu now expose one Open Log Folder action targeting `C:\ProgramData\AteraSnipeSync`, which contains Logs, History, and Preflight artifacts.
- removed the Dashboard reinstall-enabled reducer output and updated availability so NotInstalled disables Restart while Stopped enables the same fixed-label Restart button.

Tests changed:

- updated reducer tests for the single service action and fixed stopped-state behavior.
- added a controlled-path test proving ProgramDataRoot owns the Logs, History, and Preflight child roots without opening Explorer or writing ProgramData.

Documentation changed:

- updated the TrayApp 功能职责, 技术规格, and 单元测试指导手册 before production changes to define the new action ownership, connection-test save/reload/IPC flow, and single ProgramData folder entry.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TrayApp" --logger "console;verbosity=detailed"
dotnet test AteraSnipeSync.sln --no-build --no-restore --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
TrayApp tests: 35 passed, 0 failed, 0 skipped
All tests: 276 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: launch the TrayApp and confirm the reduced AntdUI action layout, fixed Restart Service text while SCM is Stopped, Test Connections placement/result styling in API & Credentials, and that both Open Log Folder entry points open `C:\ProgramData\AteraSnipeSync`. No API endpoint, DTO, payload, authentication, or pagination code changed, and automated tests did not call real APIs or mutate a real Windows Service.

## 2026-07-21 Concrete Email/Webhook Notifications and Test Button

Audited the Notification pipeline and replaced the previous no-send production registration with real standard SMTP and generic HTTPS webhook delivery.

Production code changed:

- expanded `NotificationConfig` and local JSON round-trip with SMTP host/port/TLS/optional username+password/from/to plus Webhook URL. SMTP password remains local plaintext like the current API credentials and never enters IPC/log/result text.
- added `EmailNotificationSender`, fakeable `ISmtpNotificationTransport`, and `SystemNetSmtpNotificationTransport` for one plain-text SMTP message per delivery.
- added `WebhookNotificationSender` for one HTTPS `application/json` POST containing only event type, severity, subject, message, and UTC timestamp. Non-2xx responses expose status only and response bodies are never read into results.
- added `CompositeNotificationPublisher`, which attempts every configured channel, continues after partial failure, and emits only sanitized channel-level failure summaries.
- Worker DI now registers the concrete publisher instead of `NullNotificationPublisher`; the Notifications HttpClient removes default request loggers so secret-bearing webhook URLs are not logged.
- scheduled runs and manual Preview/Sync runs now publish through the same event filter, sender set, and immutable NotificationConfig snapshot. Manual notification failure is logged separately and does not rewrite the completed sync result.
- added payload-free `TestNotifications` IPC under the global Worker run coordinator. Worker loads local notification config and returns independent Email/Webhook Configured/Succeeded status without constructing Atera/Snipe clients.
- the AntdUI Notifications tab now includes SMTP/email/webhook fields, masked SMTP password, TLS checkbox, Test Notifications button, and compact independent channel results. The test saves the complete form and reloads Worker settings before delivery.

Implementation scope:

- complete for standard SMTP with optional username/password and TLS.
- complete for generic HTTPS JSON webhook POST.
- OAuth2/Graph email, custom webhook auth/signing, Teams/Slack-specific schemas, and automatic retry remain explicit future extensions.

Tests changed:

- added offline SMTP envelope, validation, webhook JSON/HTTPS/non-2xx, partial-failure, sanitized-result, and no-channel tests.
- expanded local config save/load tests for every notification field.
- added Worker command, manual notification, and Tray IPC payload-free/result tests.
- all SMTP and webhook tests use fake transport/handlers; no real external notification was sent.

Documentation changed:

- updated Notification, WorkerScheduler, and TrayApp module plans and technical specs before production implementation.
- updated their unit test guides after the automated tests existed, including manual real-send acceptance and secret-handling rules.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Notifications|FullyQualifiedName~WorkerCommandHandlerTests|FullyQualifiedName~WorkerIpcClientTests|FullyQualifiedName~LocalAppSettingsStoreTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --logger "console;verbosity=normal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Focused notification/config/IPC tests: 66 passed, 0 failed, 0 skipped
All tests: 287 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: configure an operator-owned SMTP server and/or HTTPS webhook in the local Notifications tab, click Test Notifications, and confirm exactly one message reaches each configured channel. Do not print, log, commit, or copy SMTP password/webhook URLs into test fixtures; clear the local password afterward if it was only temporary. No real Atera/Snipe API, SMTP server, webhook endpoint, or Windows Service mutation was used by automated tests.

Server deployment clarification: notification delivery and Test Notifications execute inside the server Worker Service running as LocalSystem. Blank SMTP credentials now mean unauthenticated/IP relay and never use implicit LocalSystem Windows credentials. Manual acceptance must therefore verify server-side DNS, outbound firewall/proxy, TLS trust, and the exact SMTP port/HTTPS endpoint.

## 2026-07-21 Teams Workflow Adaptive Card Webhook Fix

Root cause:

- the first concrete webhook sender posted generic `{ eventType, severity, subject, message, occurredAtUtc }` JSON to every endpoint.
- Microsoft Teams `When a Teams webhook request is received` accepted that HTTP request, but the downstream `Post card in a chat or channel` action failed because the request did not contain an Adaptive Card whose content `type` is `AdaptiveCard`.
- the old Tray `Success` text described SMTP/HTTP acceptance and incorrectly implied final inbox/channel delivery.

Production code changed:

- added `WebhookPayloadFormat` with `TeamsAdaptiveCard` and `GenericJson`; `NotificationConfig` defaults legacy/missing format settings to Teams.
- `LocalAppSettingsStore` persists the enum name, loads missing legacy values as Teams, and rejects unknown values instead of guessing a wire contract.
- `WebhookNotificationSender` now emits the Microsoft Teams workflow envelope (`type = message`, one `application/vnd.microsoft.card.adaptive` attachment, `content.type = AdaptiveCard`, schema 1.2, safe title/facts/message body) or the explicitly selected generic five-field JSON.
- the AntdUI Notifications tab now includes a Webhook format selector. New/legacy configuration selects `Teams Workflow (Adaptive Card)`; `Generic JSON` remains available for non-Teams HTTPS endpoints.
- notification test success text now says `Accepted` and explicitly states downstream delivery is not confirmed. SMTP acceptance, a webhook HTTP 2xx, a completed Teams Flow, and final inbox/channel delivery are no longer presented as the same state.

Automated tests changed:

- added exact Teams envelope/card/body/fact assertions using an in-memory HTTP handler.
- retained the generic JSON safety test with explicit `GenericJson` configuration.
- added acceptance-not-delivery result assertions plus settings format round-trip, legacy default, and invalid-value fail-closed tests.
- no real SMTP server, Teams Flow, webhook endpoint, Atera API, Snipe-IT API, or Windows Service was contacted or mutated.

Documentation changed:

- updated Notification and TrayApp module plans and technical specs before production code.
- updated Notification and TrayApp unit test guides after tests, including server-only Teams manual acceptance and Power Automate run-history expectations.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~NotificationDeliveryTests|FullyQualifiedName~LocalAppSettingsStoreTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Focused notification/config tests: 32 passed, 0 failed, 0 skipped
All tests: 291 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: deploy the rebuilt Tray and Worker binaries to the server, restart `AteraSnipeItAutoSync`, select `Teams Workflow (Adaptive Card)`, click Test Notifications, expect `Webhook: Accepted`, then confirm both the Teams channel card and the Power Automate `Post card in a chat or channel` action. The secret Flow URL must stay only in local untracked configuration and must not be printed, logged, committed, or copied into test fixtures. An accepted trigger followed by a failed Flow action can only be diagnosed from Power Automate run history; authenticated trigger support and Flow run-history polling remain out of scope.

## 2026-07-21 Dashboard Action Clipping and Activity Consolidation

Production code changed:

- restructured `TrayDashboardForm` body from four rows to three. The left primary card now combines System status with Current activity phase, AntdUI progress, and bounded message.
- moved Automation and Latest run into a vertically stacked right column, keeping status/activity and recent results visible without a separate Current activity card.
- replaced the wrapping action `FlowLayoutPanel` with a four-column equal-width `TableLayoutPanel`; Sync Now, Preview, Cancel, and Restart Service fill their own cells.
- increased the action row to 148px after the first real control-bounds regression run showed that 136px still left the 38px button two pixels outside its 38px content parent when vertical margins were applied.
- preserved all existing action handlers, enable/disable state, IPC/service behavior, status rendering, logging, and controlled-path rules.

Automated tests changed:

- added `DashboardLayoutTests` using an STA WinForms thread, temp settings/log paths, fake service reader, and non-executing maintenance launcher.
- the test lays out the real Dashboard at MinimumSize and asserts all four action buttons remain within parent bounds, at least 38px high, and no narrower than the shared styled minimum width.
- the test also proves there is no independent Current activity card and that the sole AntdUI Progress control is nested under the System status card.
- no window is shown and no Worker pipe, SCM, UAC, Explorer, Atera API, Snipe-IT API, SMTP, or webhook operation is performed.

Documentation changed:

- updated the TrayApp module plan and technical spec before production code with the consolidated card hierarchy and four-column action contract.
- updated the TrayApp unit test guide after the regression test existed, including target-server DPI visual checks.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Dashboard minimum-size layout test: 1 passed, 0 failed, 0 skipped
All tests: 292 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: deploy the rebuilt TrayApp to the target server, open the Dashboard at the server's actual DPI/display scaling, reduce it to MinimumSize, and visually confirm complete button text/edges plus the combined System status activity section. Automated bounds checks cover the control tree but do not replace final raster/render inspection on the deployment display.

## 2026-07-21 Last Run Counts and Partial-Success Reporting

Production code changed:

- changed history report classification to `Success`, `PartialSuccess`, or `Failed`. A failed core run with any successful/unchanged asset or successful reference-resource counter now writes `PartialSuccess`; a run with no successful resource outcome remains `Failed`.
- kept the core `SyncRunResult.Success` bool unchanged for scheduler and notification behavior. `PartialSuccess` retains the first failure in `LastError` and does not advance `LastSuccessAt`.
- added `Deleted` to the status snapshot and Worker IPC summary. It is currently 0 because the product's import policy does not delete Snipe-IT assets.
- changed Dashboard `Latest run` to show only `Created`, `Updated`, `No change`, and `Deleted`, plus the finish time. Removed the displayed outcome, Pulled, Mapped, and Failed metrics from that card.
- mapped `No change` from the existing `SkippedAssets` counter, whose importer behavior means the matched Snipe-IT asset required no HTTP write.
- changed the Worker-offline history reader from a formatted result string to a structured count-only model; older history without `assetsDeleted` safely yields 0.
- Current activity now distinguishes partial completion from a pure failure when the Worker terminal summary contains both successful asset counts and a failed core outcome.

Automated tests changed:

- added report classification tests for mixed asset success/failure and reference-resource success followed by asset failure.
- added snapshot assertions for Deleted, PartialSuccess failure details, and preservation of the previous full-success timestamp.
- updated offline history reader tests for the four count-only fields and legacy missing-deleted fallback.
- expanded the real minimum-size Dashboard control-tree test to require the four new captions and reject Pulled, Mapped, and Failed captions.
- added a Worker terminal-summary assertion for `Deleted = 0`.

Documentation changed:

- updated StatusStore, WorkerScheduler, and TrayApp module plans and technical specs before production implementation.
- updated their unit test guides after the automated tests existed.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~JsonFileSyncStatusStoreTests|FullyQualifiedName~LatestSyncHistoryReaderTests|FullyQualifiedName~DashboardLayoutTests|FullyQualifiedName~WorkerCommandHandlerTests"
dotnet test AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Focused report/status/Tray/Worker tests: 35 passed, 0 failed, 0 skipped
All tests: 296 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: deploy both rebuilt TrayApp and WorkerService binaries to the server, restart `AteraSnipeItAutoSync`, run a sync with mixed successful and failed records, and confirm the JSON history report says `PartialSuccess` while Dashboard Last run shows only the four counts. No real Atera API, Snipe-IT API, SMTP server, webhook endpoint, Windows Service, or operator data was used by automated tests.

## 2026-07-21 In-place Configuration Page Navigation

Root cause:

- the old Configuration click set `DashboardLocalActivity.SavingConfiguration`, disabled Dashboard controls, and opened a modal `SyncConfigurationForm`.
- restoring the Configuration button depended on every modal `DialogResult`/close path explicitly returning the Dashboard state to Idle. A missed/non-standard close or async lifecycle path could leave Config disabled until the Tray form was recreated.

Production code changed:

- replaced the top-level `SyncConfigurationForm : AntdUI.Window` with `SyncConfigurationPage : UserControl` and renamed the source file accordingly.
- `TrayDashboardForm` now owns one fill-docked page host, its existing Dashboard view, and one reusable configuration page. Clicking Configuration hides Dashboard content and immediately shows the embedded page in the same top-level window; no `ShowDialog`, `DialogResult`, child `Close`, or second Form remains.
- added `Back to Dashboard`; Back restores the original Dashboard content/title and recalculates control state. Closing the main window while Configuration is active also resets to Dashboard before hiding to tray.
- successful Save raises an in-process event, restores Dashboard, and uses the existing `ReloadScheduleAsync` path. Save failure remains on Configuration.
- removed `DashboardLocalActivity.SavingConfiguration`; viewing a page is no longer represented as an exclusive async operation. Configuration load/save/test buttons manage their own mutually exclusive enabled state.
- reload, run, and service-maintenance cleanup now apply the restored Idle control state before awaiting the final SCM/Worker refresh, so an incidental refresh failure cannot preserve a previously disabled Config button.
- Configuration reloads local JSON on every visit. Before applying it, every reused input/checkbox/select and test-status label is reset to its declared default so deleted settings cannot remain visually stale.
- retained AntdUI tabs/inputs/buttons and the embedded PageHeader window controls; connection and notification tests keep their existing payload-free Worker IPC and sanitized logging behavior.

Automated tests changed:

- added a real STA WinForms control-tree navigation regression to prove Configuration is one `UserControl` descendant of the existing `TrayDashboardForm`, not another Form.
- the regression covers malformed local JSON with usable Back, Dashboard → Configuration → Back → Configuration repetition, reuse of the same page instance, disk reload between visits, clearing a removed credential field, Save returning to Dashboard, and Configuration re-enabling after the Worker reload/final refresh path.
- removed the obsolete reducer theory case for `SavingConfiguration`.
- all UI tests use temp settings/log paths, a fake SCM reader, a non-executing maintenance launcher, and an unavailable random Worker pipe with a short timeout. No real window is shown and no external system is contacted.

Documentation changed:

- updated the TrayApp module plan and technical specification before production changes, replacing modal-window contracts with concrete embedded-page ownership, navigation, failure, and testing contracts.
- updated the TrayApp unit test guide after the regression existed, including server-side manual visual acceptance.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~DashboardLayoutTests|FullyQualifiedName~DashboardStateReducerTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Focused Dashboard navigation/state tests: 13 passed, 0 failed, 0 skipped
All tests: 296 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: deploy the rebuilt TrayApp to the server and verify Configuration replaces Dashboard content inside the same window, retains normal minimize/maximize/close buttons, Back and Save both return to Dashboard, and Configuration can be opened repeatedly without restarting TrayApp. This refactor did not change WorkerService behavior or any external API wire contract.

## 2026-07-21 Hardcoded Unknown Mapping Fallbacks

Production code changed:

- added `SyncApplicationDefaults` with fixed `Unknown Company`, `Unknown Manufacturer`, and `Unknown Model` constants.
- removed Default company, Default manufacturer, and Default model inputs from the embedded Configuration `Mapping & Import` tab. Default category remains editable.
- Configuration build/save uses the shared constants and no longer reads/applies removed controls.
- `LocalAppSettingsStore` ignores legacy custom values for the three properties, returns constants in compatibility members, does not validate caller overrides, and removes the legacy Mapping JSON properties on save.
- `HasAnySyncAppSetting` no longer counts the always-present compatibility defaults, preserving correct null behavior for an otherwise-empty local config.
- `WorkerRuntimeFactory` assigns the shared constants directly, so an old or custom settings snapshot cannot override production mapping fallback behavior.

Automated tests changed:

- changed local settings persistence assertions to require the three legacy JSON properties to be absent and reloaded compatibility values to equal the constants.
- added a legacy-custom-value load test proving normalization to the constants.
- expanded Worker runtime composition coverage with conflicting custom settings and fixed mapping assertions.
- expanded the real Configuration control-tree test to prove all three removed AntdUI inputs are absent.
- no real Atera/Snipe API or external service was used.

Documentation changed:

- updated WorkerScheduler and TrayApp module plans and technical specifications before production code.
- updated their test guides after the automated tests existed.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~LocalAppSettingsStoreTests|FullyQualifiedName~WorkerRuntimeFactoryTests|FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
Solution build: succeeded, 0 warnings, 0 errors
Focused config/runtime/Dashboard tests: 26 passed, 0 failed, 0 skipped
All tests: 297 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: deploy the matching TrayApp, Core, and WorkerService binaries to the server, save Configuration once, and confirm the three Unknown inputs are absent and the next local JSON no longer contains their legacy Mapping keys. Existing custom values are ignored immediately by the new Worker runtime even before that cleanup save.

## 2026-07-22 AntdUI Demo Visual Alignment

Production code changed:

- removed the complete dark Dashboard hero, including the `Atera → Snipe-IT` heading and synchronization-center subtitle.
- changed the Dashboard root to a compact two-row shell with an AntdUI `PageHeader` showing `Auto Sync` / `Dashboard`; moved the existing Configuration action into the header and added a settings icon.
- aligned the shared light palette with Ant Design tokens, changed cards to a flat 1px-border/radius-8 treatment, changed buttons/inputs/selects to radius 6, and added bounded action icons.
- replaced all seven Configuration AntdUI checkboxes with 44×24 AntdUI switches while preserving their existing checked defaults, load/apply behavior, settings mapping, validation, save flow, Worker IPC, and schedule reload behavior.
- gave each switch a stable semantic name and retained the descriptive setting text in the grid's left label column.

Automated tests changed:

- expanded `DashboardLayoutTests` to require the `Auto Sync` / `Dashboard` header and reject the removed arrow hero label.
- added control-tree assertions that Configuration contains no AntdUI Checkbox and contains exactly the seven expected named AntdUI Switch controls with fixed minimum size.
- retained the existing minimum-size action bounds, card ownership, Latest run captions, reusable Configuration navigation, settings reload, and Save-return regression coverage.

Documentation changed:

- updated the TrayApp module plan and technical specification before production changes with the exact header, palette, switch names/sizing, unchanged behavior boundaries, and regression requirements.
- updated the TrayApp unit test guide after the focused tests passed, including target-DPI manual visual acceptance.

Verification commands:

```powershell
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tray\
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests\ --logger "console;verbosity=minimal"
git diff --check
```

Latest known result:

```text
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
All tests: 297 passed, 0 failed, 0 skipped
```

The normal solution output path was not overwritten because the operator's existing TrayApp process had the EXE locked; verification used isolated temporary output directories and did not stop that process. Remaining manual-only verification: close the old TrayApp, start the rebuilt TrayApp, and inspect the Dashboard and all four Configuration tabs at the target display scaling. Confirm the compact header, flat cards, action icons, seven blue switches, label alignment, and absence of clipping or overlap. No automated test called real Atera, Snipe-IT, SMTP, webhook, Worker, SCM, UAC, or Explorer operations.

## 2026-07-22 Dashboard Card and Configuration Density Refinement

Production code changed:

- renamed the Dashboard `Automation` card to `Schedule` without changing its schedule state or next-run data.
- split the left Dashboard column into independent `System status` and `Current activity` cards. Removed the duplicate Current operation row and its private label while retaining Worker active-operation state for reducer, busy, formatter, and activity-message behavior.
- removed the `Run and service actions` title and title-row whitespace. The four existing action buttons now occupy one titleless bordered surface.
- added a shared titleless AntdUI surface factory so title-free sections retain the same border, background, radius, margin, and explicit content padding as normal cards.
- placed Configuration inside a padded PageHeader action host, increased the header height, restored radius 6, and gave the button a 176×40 minimum size. Open Log Folder now has a fixed/minimum width of 176px.
- increased Configuration single-line input/select/test-button height to 44px, multiline input height to 96px, Switch size to 56×32, value-row vertical margin to 9px, and label vertical padding to 10px.
- increased the Configuration footer to 84px with an explicit one-row percent layout. Back/Save have 46px minimum height, wider columns and DPI-safe icon/text padding so TableLayout cannot compress them to preferred-height slivers.

Automated tests changed:

- renamed the minimum-size layout regression to match the restored separate activity card.
- now requires the progress control to belong to Current activity, rejects Current operation/Automation/Run and service actions labels, requires Schedule, and proves all four run buttons share one titleless surface.
- verifies Configuration and Open Log Folder bounds, minimum widths, icons and rounded hit areas at Dashboard MinimumSize.
- verifies all seven switches have 56×32 minimum size, single-line/multiline inputs retain 44/96px height, and Back/Save retain at least 46px minimum height.

Documentation changed:

- updated the TrayApp module plan and technical specification before production changes with the card ownership, titleless surface, long-button sizing, Configuration density and footer sizing contracts.
- updated the TrayApp test guide after focused tests passed with the new assertions and target-DPI visual checks.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests2\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests2\ --logger "console;verbosity=minimal"
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tray2\
git diff --check
```

Latest known result:

```text
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
All tests: 297 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: close the old running TrayApp, start the rebuilt output, and inspect Dashboard plus all four Configuration tabs at the deployment DPI. Confirm the separate Current activity card, Schedule title, titleless four-button row, complete Configuration/Open Log Folder text and icons, taller switches/fields, and full-height Back/Save buttons. No external API, notification endpoint, Worker command, SCM action, UAC process, or Explorer operation was used by automated verification.

## 2026-07-22 Schedule Frequency-Aware Configuration Fields

Production code changed:

- named the AntdUI frequency selector `ScheduleFrequency` and subscribed to its `SelectedValueChanged` event after all Schedule rows are created.
- added frequency-aware row visibility: Daily hides Weekly days, Monthly days and the last-day switch; Weekly shows only Weekly days; Monthly shows Monthly days and the last-day switch.
- hides and shows both the label and value control so AutoSize rows collapse completely. Frequency, Schedule enabled, Windows time zone ID and Run times remain visible for every frequency.
- switching frequency does not clear hidden values and does not change `SyncScheduleOptions`, validation, JSON persistence or Worker scheduling behavior.
- confirmed the TimeZone field default still comes from `TimeZoneInfo.Local.Id`; a nonblank saved `Schedule.TimeZoneId` continues to override that default when Configuration loads.

Automated tests changed:

- expanded the existing STA Configuration control-tree regression to select the real Schedule tab and exercise Daily, Weekly and Monthly through the AntdUI Select.
- asserts the local WinForms Visible state for each conditional value and its paired label, plus the always-visible TimeZone input, without showing the top-level form.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with the exact visibility matrix, value-preservation rule and system-time-zone default/persisted override behavior.
- updated the TrayApp test guide after focused tests passed with automated and manual time-zone checks.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests3\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests3\ --logger "console;verbosity=minimal"
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tray3\
git diff --check
```

Latest known result:

```text
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
All tests: 297 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: restart the rebuilt TrayApp, open Schedule, switch through Daily/Weekly/Monthly and confirm rows collapse without visual gaps at the deployment DPI. With no persisted Schedule, confirm Windows time zone ID equals the current machine's `TimeZoneInfo.Local.Id`; after saving another valid Windows time-zone ID, confirm reload preserves that configured value. Automated verification did not call external APIs, Worker IPC, SCM, UAC, notification endpoints or Explorer.

## 2026-07-22 Dashboard Footer Wording Clarification

Production code changed:

- changed the Dashboard footer from `Detailed activity is written to daily log files.` to `Detailed activity is available in the log folder.` so the status hint does not imply a Daily Schedule frequency.
- restored the Mapping & Import `DryRun` switch label to `Dry run for scheduled/manual Sync Now` after the earlier screenshot clarification; its property, default, persistence and no-write behavior remain unchanged.

Automated tests changed:

- the Dashboard control-tree regression requires exactly one new footer label and rejects the former daily-log-files wording.
- the Configuration control-tree regression again requires the restored `Dry run for scheduled/manual Sync Now` label.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with the footer wording and unchanged log-rotation behavior boundary.
- updated the TrayApp test guide after the focused Dashboard layout tests passed.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests5\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tests5\ --logger "console;verbosity=minimal"
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-ui-tray5\
git diff --check
```

Latest known result:

```text
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
All tests: 297 passed, 0 failed, 0 skipped
```

Remaining manual-only verification: restart the rebuilt TrayApp and confirm the Dashboard footer shows `Detailed activity is available in the log folder.` without clipping. No external service was contacted by automated verification.

## 2026-07-22 Notification Event Selection Switches

Production code changed:

- replaced the Notifications tab's free-form `On events (;)` AntdUI input with eight explicit AntdUI switches for scheduled sync, manual sync, manual preview and fallback sync completed/failed events.
- added one ordered event/switch catalog in `SyncConfigurationPage`; the same catalog renders rows, restores persisted selections, resets the form and writes canonical `NotificationEventTypes` values to the existing `NotificationConfig.OnEvents` list.
- existing event names load case-insensitively after trimming; unknown legacy values select nothing and are not guessed. The local JSON schema, Worker `NotificationEventFilter`, webhook payload and sender behavior did not change.
- `Notifications enabled` remains the master gate. No selected event means normal notifications remain suppressed, while the explicit `Test Notifications` command continues to bypass event filtering.

Automated tests changed:

- expanded the STA Configuration control-tree regression to require all eight named 56×32 event switches and reject the removed `NotificationEvents` input.
- added case-insensitive selection, canonical output, unknown-value and empty-selection assertions through the same mapping methods used by production load/save behavior.
- all tests remain offline and did not invoke Worker IPC, SMTP, Teams, webhook, Atera or Snipe-IT endpoints.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with the exact switch/event/label mapping, compatibility behavior and manual-test bypass boundary.
- updated the TrayApp unit test guide after the focused regression passed, including manual save/reload acceptance steps.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-notification-event-switches\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-notification-event-switches\ --filter "FullyQualifiedName~WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-notification-event-switches\ --filter "FullyQualifiedName!~WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-notification-event-switches-tray\
git diff --check
```

Latest known result:

```text
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
Non-flaky remainder: 304 passed, 0 failed, 0 skipped
Worker IPC timing test standalone rerun: 1 passed, 0 failed, 0 skipped
Combined 305-test invocation: 304 passed and the same Worker IPC timing test failed with a transient broken pipe; it passed immediately when isolated and is unrelated to this Tray-only change
git diff --check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: restart the rebuilt TrayApp, open Configuration → Notifications, select a few event switches, save and reopen Configuration to confirm the same selections return. Run matching and nonmatching sync outcomes to confirm only selected events notify; verify `Test Notifications` still sends with every event switch off. No real notification or external API was used by automated verification.

## 2026-07-22 Configuration Cancel Label

Production code changed:

- changed the Configuration footer navigation button text from `Back to Dashboard` to `Cancel`.
- retained the existing left-arrow icon, button dimensions, enabled-state handling and `DashboardRequested` behavior. Cancel still returns to Dashboard without saving, schedule reload or Worker IPC.

Automated tests changed:

- renamed the Configuration navigation regression to `ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel`.
- require exactly one Config-page `Cancel` button, reject the former `Back to Dashboard` text and click the real Cancel button to verify Dashboard return and page reuse.
- scoped Dashboard run-action layout assertions to the Dashboard action surface because both Dashboard and Configuration now legitimately contain a button named `Cancel`.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with the Cancel wording and unchanged no-save navigation boundary.
- updated the TrayApp unit-test guide after the focused regression passed, including the duplicate-label scoping rationale and manual acceptance step.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-config-cancel\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-config-cancel\ --logger "console;verbosity=normal"
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-config-cancel-tray\
```

Latest known result:

```text
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
All tests: 305 passed, 0 failed, 0 skipped
TrayApp isolated-output build: succeeded, 0 warnings, 0 errors
```

Remaining manual-only verification: restart the rebuilt TrayApp, open Configuration, change a field without saving, click Cancel, reopen Configuration and confirm the unsaved value was discarded. Automated verification did not contact Worker, SCM, Atera, Snipe-IT, SMTP or webhook endpoints.

## 2026-07-22 Remove Configurable Scheduled Dry-Run

Production code changed:

- removed the `DryRun` switch and label from Configuration → Mapping & Import, including load, reset and save mappings.
- removed scheduled dry-run from `SyncAppSettings`, legacy `SyncConfig`, `ILocalAppSettingsReader` and `LocalAppSettingsStore`; saving now removes an existing `Sync.DryRun` JSON member while preserving other `Sync` properties.
- fixed both `WorkerRuntimeFactory` base requests and `ScheduledSyncRequestFactory` clones to `Sync.DryRun = false` and `SnipeIt.DryRun = false`. Scheduled runs are therefore always real syncs and never generate manual preflight CSV.
- retained run-level/import-level DryRun contracts and importer behavior for Dashboard Preview, which remains fixed `DryRun = true` and performs no Snipe-IT writes. Sync Now remains fixed real sync.
- removed `DryRun` from the sample local JSON and updated environment/master-plan configuration guidance so it is no longer presented as an operator setting.

Automated tests changed:

- Configuration control-tree coverage rejects a `DryRun` switch and any `Dry run` label while retaining the six base switches and eight notification-event switches.
- local-settings coverage proves new saves omit `Sync.DryRun` and a save migrates a legacy `DryRun = true` file by removing only that field.
- runtime, scheduled request-factory and scheduler tests prove scheduled requests are non-dry-run; the request-factory test deliberately starts from a dry-run base request.
- removed obsolete fake-reader dry-run methods and preserved existing Preview/Sync Now request-factory coverage.

Documentation changed:

- updated WorkerScheduler and TrayApp module plans and technical specifications before production code with the new fixed-real scheduled behavior and preserved Preview boundary.
- updated both module test guides after the focused tests passed, including offline regression and safe local manual checks.
- updated the environment setup, master plan and sample configuration to remove the obsolete operator setting.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-remove-scheduled-dryrun\ --filter "FullyQualifiedName~DashboardLayoutTests|FullyQualifiedName~LocalAppSettingsStoreTests|FullyQualifiedName~WorkerRuntimeFactoryTests|FullyQualifiedName~ScheduledSyncRequestFactoryTests|FullyQualifiedName~WorkerSchedulerTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-remove-scheduled-dryrun\ --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-remove-scheduled-dryrun\ --filter "FullyQualifiedName~WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-remove-scheduled-dryrun\ --filter "FullyQualifiedName!~WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-remove-scheduled-dryrun-build\
Get-Content -Raw -Encoding UTF8 samples\configs\appsettings.local.example.json | ConvertFrom-Json | Out-Null
git diff --check
```

Latest known result:

```text
Focused affected tests: 30 passed, 0 failed, 0 skipped
Non-flaky remainder: 304 passed, 0 failed, 0 skipped
Known Worker IPC timing test standalone rerun: 1 passed, 0 failed, 0 skipped
Combined 305-test invocation: 304 passed; the same known timing test transiently failed with Pipe is broken and passed immediately in isolation
Solution isolated-output build: succeeded, 0 warnings, 0 errors
Sample JSON parse: passed
git diff --check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: restart the rebuilt TrayApp, confirm Mapping & Import has no Dry Run row, save once, and confirm the local untracked JSON no longer contains `Sync.DryRun`. Because scheduled runs now perform real Snipe-IT writes, verify the schedule and mapping settings before enabling the schedule. Automated verification did not contact Worker, SCM, Atera, Snipe-IT, SMTP or webhook endpoints.

## 2026-07-22 Classify Completed Runs Separately from Record Failures

Production code changed:

- changed `SyncOrchestrator` so `SyncRunResult.Success` means the pull → mapping → import pipeline returned normally and was not cancelled. Import asset/reference failures remain in `ImportResult.Failures`, run-level `Failures` and failed counters without downgrading a completed run.
- retained failed results for pull/mapping/import exceptions and `SnipeImportResult.Cancelled = true`; cancellation exceptions still propagate to the Worker/UI cancellation path.
- changed `NotificationRequestFactory` so completed runs with record failures produce `*SyncCompleted`, `Warning` severity and `Result: Completed` while retaining failed counts and first-failure details. Incomplete/fatal runs still produce `*SyncFailed` and `Result: Failed`.
- removed new-report `PartialSuccess` classification from `JsonFileSyncStatusStore`. Completed runs write `Success`; incomplete/fatal runs write `Failed` even if some resource actions completed before interruption.
- removed Dashboard partial-success rendering. Completed structured results show `Sync completed.` / `Completed`; fatal results show `Sync failed.`; cancelled results remain separate.

Automated tests changed:

- Orchestrator coverage proves record failures preserve `Success = true`, while a cancelled import result is failed.
- notification factory and Worker command integration coverage prove completed-with-record-failures publishes `ScheduledSyncCompleted`/`ManualSyncCompleted` with Warning severity and safe failure details.
- Status Store coverage proves completed-with-record-failures writes `Success` and advances `LastSuccessAt`, while incomplete results with successful asset/reference counters write `Failed`.
- no automated test called Atera, Snipe-IT, SMTP or webhook endpoints.

Documentation changed:

- updated module plans and technical specs for SyncOrchestrator, StatusStore, Notification, WorkerScheduler and TrayApp with the pipeline-completion contract and fatal/incomplete boundary.
- updated the corresponding unit-test guides after the regression tests existed and passed.
- retained compatibility for reading old history documents containing `PartialSuccess`; new runs no longer generate or display that outcome.

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SyncOrchestratorTests|FullyQualifiedName~JsonFileSyncStatusStoreTests|FullyQualifiedName~NotificationRequestFactoryTests" --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~WorkerCommandHandlerTests.ExecuteAsync_SyncNow_PublishesCompletedNotification_WhenCompletedRunHasRecordFailures" --no-restore --logger "console;verbosity=normal"
dotnet test AteraSnipeSync.sln --no-restore --logger "console;verbosity=minimal"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=normal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --filter "FullyQualifiedName!=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental
git diff --check
```

Latest known result:

```text
Focused Orchestrator/Status/Notification tests: 51 passed, 0 failed, 0 skipped
Worker completed-notification integration test: 1 passed, 0 failed, 0 skipped
Non-flaky full remainder: 306 passed, 0 failed, 0 skipped
Known Worker IPC timing test standalone rerun: 1 passed, 0 failed, 0 skipped
Combined 307-test invocation: 306 passed; the existing timing test transiently failed with Pipe is broken and passed immediately in isolation
Solution build: succeeded, 0 warnings, 0 errors
git diff --check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: deploy/restart the rebuilt Worker and Tray, run a sync that finishes with at least one record-level import failure, and confirm Current activity says `Sync completed.`, the configured completed notification is sent with Warning severity, and history writes `Success` while retaining the failed count/details. A deliberately interrupted run should instead show/send Failed; a hard process crash may not reach notification delivery and must never be presented as completed.

## 2026-07-22 Merge Notification Events into Completed/Failed Switches

Production code changed:

- replaced the eight per-trigger notification event switches in `SyncConfigurationPage` with two outcome switches: `Sync completed` and `Sync failed`.
- kept the existing `NotificationConfig.OnEvents` JSON schema and Worker exact-match filter. Completed expands to Scheduled/Manual/Preview/Other completed event names; Failed expands to the corresponding four failed names.
- loading a legacy partial selection now enables its outcome group using trimmed case-insensitive matching. Saving then normalizes that selected group to all four canonical event names; unknown legacy values are ignored.
- kept `Notifications enabled` as the master switch and kept Test Notifications independent from normal event filtering.

Automated tests changed:

- Dashboard Configuration control-tree coverage now requires only `NotifySyncCompleted` and `NotifySyncFailed`, rejects the removed six per-trigger switches through the exact switch catalog, and verifies the two visible labels.
- event mapping coverage proves one legacy completed event plus one legacy failed event expands to all eight canonical events; clearing both switches returns an empty event list.
- tests remain local and do not call Worker, Atera, Snipe-IT, SMTP or webhook endpoints.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with the two-group mapping and legacy normalization rule.
- updated the TrayApp unit-test guide after focused tests passed.

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~DashboardLayoutTests" --no-restore --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --filter "FullyQualifiedName!=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental
git diff --check
```

Latest known result:

```text
Focused Dashboard/Configuration tests: 2 passed, 0 failed, 0 skipped
Non-flaky full remainder: 306 passed, 0 failed, 0 skipped
Known Worker IPC timing test standalone: 1 passed, 0 failed, 0 skipped
Solution build: succeeded, 0 warnings, 0 errors
git diff --check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: restart the rebuilt TrayApp, open Configuration → Notifications, confirm only `Sync completed` and `Sync failed` appear below the master switch, save each combination and reopen the page to confirm it persists. No real notification needs to be sent for this UI check.

## 2026-07-23 Dashboard Contrast and Density Refinement

Production code changed:

- changed the shared light canvas from `#F5F5F5` to `#F1F3F6` and the surface border from `#F0F0F0` to `#D9DEE7`, making white Dashboard surfaces visibly distinct without introducing a dark theme.
- added a 6px, 12%-opacity, 2px-down shadow to titled cards and the action surface; retained 8px radius and a 1px neutral border.
- gave secondary Dashboard buttons an explicit 1px border, white default background and subtle hover/active fills. Primary, Danger, disabled states and handlers are unchanged.
- reduced Dashboard default/minimum height, header/body padding, surface margins and card padding; reduced the action row from 148px to 100px, the footer from 58px to 48px and action gutters from 12px to 8px between adjacent buttons.
- tightened Latest run metric top spacing/tile gutters and added a 1px tile border while preserving all four count values.

Automated tests changed:

- minimum-size Dashboard coverage now locks the 920×650 minimum, compact action surface height/padding, card/action shadow, border, margin and card padding.
- secondary button coverage requires visible borders for Preview, Restart Service and Open Log Folder while retaining all existing no-clipping, ownership and caption assertions.
- tests construct only local controls and do not call Worker, SCM, Atera, Snipe-IT, SMTP or webhook endpoints.

Documentation changed:

- updated the TrayApp module plan and technical specification before production code with exact contrast, shadow and density tokens.
- updated the TrayApp unit-test guide after the focused regression passed.

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-dashboard-density\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-dashboard-density\ --filter "FullyQualifiedName!=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-dashboard-density\ --filter "FullyQualifiedName=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental
git diff --check
```

Latest known result:

```text
Focused Dashboard layout tests: 2 passed, 0 failed, 0 skipped
Non-flaky full remainder: 306 passed, 0 failed, 0 skipped
Known Worker IPC timing test standalone: 1 passed, 0 failed, 0 skipped
Default-output solution build after TrayApp closed: succeeded, 0 warnings, 0 errors
git diff --check: passed (line-ending conversion warnings only)
```

The first default-output test build could not copy the TrayApp executable because the operator's running TrayApp held it open. The application was not terminated by automation; verification continued in an isolated temp output, then the operator closed TrayApp and the default-output solution build passed.

Remaining manual-only verification: start the rebuilt TrayApp and visually confirm the soft card shadows, visible secondary-button borders and reduced Dashboard whitespace at the operator's normal DPI. No real sync or notification is required for this visual check.

## 2026-07-23 Asset Update Changed-Field Logging

Production code changed:

- reused each asset plan's stable `ChangeReasons` after a successful `PATCH /hardware/{id}` instead of recalculating the diff during execution.
- added one structured importer information log per successful asset update with the Snipe-IT asset id, asset tag and fixed changed-field labels.
- added the same changed-field labels to the executed import action/history message and to per-record progress, which the Tray writes to `ManualSync_yyyyMMdd.log` for manual runs.
- kept log details limited to the fixed labels `AssetTag`, `Status`, `Model`, `Company`, `Name`, `Serial`, `MacAddress` and `Notes`; notes content, field values, tokens and secrets are not logged. Failed PATCH requests do not emit a successful changed-field entry.

Automated tests changed:

- added `ImportAsync_LogsChangedFieldsAfterSuccessfulAssetUpdate` to prove the mocked successful PATCH produces the same stable `Name, MacAddress, Notes` details in `ILogger<SnipeImporter>`, per-record progress and executed action/history.
- added `ImportAsync_DoesNotLogChangedFields_WhenAssetUpdateFails` to prove mocked Snipe-IT business failure produces no successful changed-field log, progress entry or executed action.
- all HTTP traffic remains local through `StubHttpMessageHandler`; no real Atera or Snipe-IT API and no real token were used.

Documentation changed:

- rechecked the official Snipe-IT `PATCH /hardware/{id}` documentation before implementation.
- updated the SnipeImport 功能职责 and 技术规格 before production code with the logging/audit contract.
- updated the SnipeImport unit-test guide after the focused tests passed.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportAsync_LogsChangedFieldsAfterSuccessfulAssetUpdate|FullyQualifiedName~ImportAsync_DoesNotLogChangedFields_WhenAssetUpdateFails" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SnipeImporterTests" --logger "console;verbosity=minimal"
dotnet test AteraSnipeSync.sln --no-build --no-restore --filter "FullyQualifiedName!=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName=AteraSnipeSync.Tests.WorkerService.WorkerIpcServerTests.CompleteCommand_CanRunLongerThanRequestReadTimeout" --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental
git diff --check
```

Latest known result:

```text
Focused changed-field logging tests: 2 passed, 0 failed, 0 skipped
All SnipeImporter tests: 84 passed, 0 failed, 0 skipped
Full remainder excluding the known Worker IPC timing test: 308 passed, 0 failed, 0 skipped
Known Worker IPC timing test standalone: failed twice with the pre-existing intermittent System.IO.IOException: Pipe is broken at WorkerIpcServerTests.cs:204
Solution build: succeeded, 0 warnings, 0 errors
git diff --check: passed (line-ending conversion warnings only)
```

No real-key API verification was run or required. The asset-update logging requirement is complete; the unrelated Worker IPC timing-test flake remains a separate repository gap.

## 2026-07-23 Windows 11 / Server 2022 UI Consistency

Production code changed:

- changed the public Dashboard base from `AntdUI.Window` to `AntdUI.BorderlessForm` and forced the non-DWM AntdUI region/shadow path so Windows 11 and Windows Server 2022 use the same normal-window rounding behavior.
- added shared window/surface/nested-surface/control radius tokens, a soft window shadow token, and explicit 1px window border styling in `TrayUiTheme`.
- explicitly configured TrayApp for `PerMonitorV2` while retaining `AutoScaleMode.Dpi` and the existing default font.
- changed Sync Now, Preview, Cancel and Restart Service from stretched table-cell fills to centered 156×40 logical buttons; Configuration/Open Log Folder dimensions and all action semantics remain unchanged.
- replaced remaining standard control radius literals in Dashboard/Configuration with the shared control-radius token; the intentionally distinct Tabs card radius remains unchanged.

Automated tests changed:

- expanded the real STA Dashboard layout regression to verify the exact `BorderlessForm` base, disabled DWM path, window border/shadow/radius and surface/nested/control radius hierarchy.
- run-action layout is now exercised at both 920×650 and 1560×1187; all four buttons must remain centered, unclipped, identical in size and unchanged after expansion.
- tests remain local and offline: no real window is shown and no Worker, SCM, Atera, Snipe-IT, SMTP or webhook endpoint is contacted.

Documentation changed:

- updated the TrayApp 功能职责 first with the cross-OS appearance responsibility and the logical-size-not-pixel-size DPI boundary.
- updated the TrayApp 技术规格 before production code with concrete base types, theme members, property values, button layout and regression assertions.
- updated the TrayApp unit-test guide after the regressions passed with automated coverage, 100%/150% Win11/Server 2022 manual checks and common failure diagnosis.

Verification commands:

```powershell
dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore --no-dependencies -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-cross-os-ui-tray-only\
dotnet build tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --no-dependencies -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-cross-os-ui-tests-compile\
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-cross-os-ui-tests-compile\ --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=normal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-cross-os-ui-tests-compile\ --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental -m:1 -nr:false
git diff --check
```

Latest known result:

```text
TrayApp isolated no-dependencies build: succeeded, 0 warnings, 0 errors
Tests isolated no-dependencies compilation: succeeded, 0 warnings, 0 errors
Focused Dashboard layout/navigation tests: 2 passed, 0 failed, 0 skipped
Complete compiled offline test assembly: 309 passed, 0 failed, 0 skipped
Solution source rebuild: blocked by six pre-existing/in-progress SnipeImport compile errors for missing PlannedAssetDeletion and SnipeImportPlanningResult types
git diff --check: passed (line-ending conversion warnings only)
```

Remaining verification is manual-only: run the same rebuilt TrayApp on Windows 11 and Windows Server 2022 at 100% and 150% display/RDP scaling; confirm normal-window corners, restore/maximize/resize behavior, compact action buttons and Configuration layout. No real-key API test was run or required. A clean solution source rebuild must be rerun after the unrelated SnipeImport planning types are restored/completed.

## 2026-07-23 Tray and Executable Product Icon

Production code and assets changed:

- added the transparent blue “device asset + bidirectional sync” icon as a 512px master PNG, 256px PNG and multi-resolution ICO under `src/AteraSnipeSync.TrayApp/Assets`.
- configured `ApplicationIcon` so the Windows apphost `AteraSnipeSync.TrayApp.exe` carries `Assets/tray-icon.ico`.
- embedded that same ICO under the fixed logical resource name `AteraSnipeSync.TrayApp.Assets.tray-icon.ico` and added `TrayIconLoader` to return a caller-owned icon without loose-file or network access.
- replaced `SystemIcons.Application` in `TrayApplicationContext` with the bundled product icon and explicitly disposes the owned icon after `NotifyIcon` disposal.

Automated tests changed:

- added `TrayIconLoaderTests` to parse the ICO directory and require exactly the 16, 20, 24, 32, 48, 64, 128 and 256px square frames.
- added runtime loading coverage for the exact manifest resource name, transparent corner and opaque blue content.
- all tests remain local and do not show a real notification icon, open Dashboard, contact Worker/SCM, or call Atera, Snipe-IT, SMTP or webhook endpoints.

Documentation changed:

- updated the TrayApp 功能职责 first with the single-resource EXE/tray icon boundary and ownership requirements.
- updated the TrayApp 技术规格 before production code with exact project properties, resource name, loader signature, disposal order and regression cases.
- updated the TrayApp unit-test guide after the focused tests passed with automated checks, EXE extraction guidance and light/dark taskbar manual acceptance.

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-tray-icon-tests\ --filter "FullyQualifiedName~TrayIconLoaderTests" --logger "console;verbosity=minimal"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-tray-icon-tests\ --logger "console;verbosity=minimal"
dotnet build AteraSnipeSync.sln --no-restore --no-incremental -m:1 -nr:false -p:OutDir=C:\Users\TimHuang\AppData\Local\Temp\AteraSnipeSync-tray-icon-build\
[System.Drawing.Icon]::ExtractAssociatedIcon(<isolated-output>\AteraSnipeSync.TrayApp.exe)
git diff --check
```

Latest known result:

```text
Focused TrayIconLoader tests: 2 passed, 0 failed, 0 skipped
Complete compiled offline test assembly: 317 passed, 0 failed, 0 skipped
Solution isolated-output build: succeeded, 0 warnings, 0 errors
Built EXE icon extraction: succeeded; 32×32 frame contained 150 opaque blue pixels
```

Remaining verification is manual-only: close any old TrayApp process, deploy/start the rebuilt executable, and confirm both Explorer/shortcut and notification-area surfaces show the same product icon on light and dark taskbars. Explorer may require icon-cache refresh or a clean publish directory before an updated icon appears. No real-key API verification was run or required.

## 2026-07-23 v1.0.0 Windows x64 Installer

Production/release code changed:

- fixed the test-only `WorkerIpcServerTests.SendAsync` request path to write one UTF-8 line directly and flush explicitly, eliminating the `StreamWriter.Dispose` second-flush race after the server closes the pipe; production IPC code is unchanged.
- made the timing regression robust while preserving its contract: command execution is 750 ms and request-read timeout is 500 ms.
- fixed ProductVersion `1.0.0`, Assembly/FileVersion `1.0.0.0`, and Company `Vue IT Inc.` in shared build metadata.
- added self-contained `win-x64` publish metadata and pinned Worker/Tray runtime framework `10.0.10`; Worker uses the same WindowsDesktop framework baseline as Tray so merged publish files are byte-identical.
- added the WiX 7 Installer project, fixed product/upgrade identities, per-machine x64 layout, LocalSystem automatic Worker service, all-users Start Menu shortcut, HKLM Tray startup, ProgramData directories and ACLs.
- added secure opt-in `REMOVELOCALDATA`, the default-unchecked interactive uninstall dialog, remembered HKLM ProgramData path, and Util `RemoveFolderEx` with exact upgrade-safe condition `REMOVELOCALDATA=1 AND REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`.
- added `scripts/Build-Release.ps1` with clean-tree enforcement, explicit `-AllowDirty` development mode, locked restore, Release build/test, two self-contained publishes, SHA-256 collision checking, forbidden-file gates, UUID v5 ProductCode, MSI build, checksum and manifest output.

Automated tests changed:

- added 8 source-level Installer contract tests for identity, service, Tray startup/shortcut, ProgramData ACL, uninstall property/dialog/RemoveFolderEx safety, release gates and forbidden files.
- Installer tests do not execute MSI, mutate SCM/HKLM, or call external APIs.

Documentation changed:

- added `docs/module-plans/09-Installer-功能职责.md` before production code.
- added `docs/technical-specs/09-Installer-技术规格.md` before production code.
- added `docs/test-guides/09-Installer-单元测试指导手册.md` after tests, including the disposable Windows 11/Server 2022 VM matrix.
- updated `README.md` with release, install, silent-uninstall and local-data-preservation commands.

Verification commands:

```powershell
dotnet restore AteraSnipeSync.sln
dotnet restore installer/AteraSnipeSync.Installer/AteraSnipeSync.Installer.wixproj
dotnet build AteraSnipeSync.sln --configuration Release --no-restore
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~InstallerContractTests
# CompleteCommand_CanRunLongerThanRequestReadTimeout executed in a 20-run loop.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -AllowDirty
git diff --check
```

Latest known result:

```text
Release solution build: succeeded, 0 warnings, 0 errors
Installer contract tests: 8 passed, 0 failed, 0 skipped
Named Pipe focused flaky gate: 20 consecutive runs passed
Complete release test assembly: 325 passed, 0 failed, 0 skipped (original 317 plus 8 Installer contracts)
Worker/Tray self-contained publish: succeeded; merged with no differing-hash collision after runtime alignment
WiX package build: blocked by WIX7015 before source compilation because the project owner has not yet explicitly accepted the WiX 7 OSMF EULA
git diff --check: passed (line-ending conversion warnings only)
```

Remaining release gates:

- project owner must review the official WiX 7 OSMF EULA and explicitly authorize acceptance; automation must not accept it on the owner's behalf.
- after authorization, add the explicit WiX 7 acceptance, compile/validate the MSI, perform administrative extraction/version/content/hash/manifest checks, and rebuild from the clean release commit.
- run the documented Windows 11 x64 and Windows Server 2022 x64 VM install/repair/uninstall/upgrade matrix. No local tag is created until both VM matrices pass; no push is automatic.
