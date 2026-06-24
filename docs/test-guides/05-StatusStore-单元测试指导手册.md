# Status Store - 单元测试指导手册

## 1. 范围

本指南覆盖 Module 5 Status Store 的本地文件持久化测试。

实现文件：

- `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Status/SyncStatusStoreOptions.cs`
- `src/AteraSnipeSync.Core/Status/SyncHistory*.cs`
- `src/AteraSnipeSync.Core/Status/Interfaces/ISyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Status/SyncStatusSnapshot.cs`

测试文件：

- `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`

## 2. 测试命令

从仓库根目录运行：

```powershell
dotnet build
dotnet test
```

当前已验证结果：

```text
dotnet build: succeeded, 0 warnings, 0 errors
dotnet test: Passed: 93, Failed: 0, Skipped: 0
```

## 3. Mocking / Fixture 策略

Status Store 测试不使用 HTTP client，不访问 Atera API，不访问 Snipe-IT API，也不读取真实 API key。

测试通过以下方式构造输入：

- 手写 `SyncRunResult`
- 手写 `AteraPullResult`
- 手写 `SnipeImportBatch`
- 手写 `SnipeImportResult`
- 手写 `ImportAction` / `ImportFailure`
- 每个测试使用临时 history directory

日志使用 `NullLogger<JsonFileSyncStatusStore>`。测试结束后删除临时目录。

## 4. 覆盖用例

已实现的 Status Store 测试：

1. `SaveAsync_WritesSuccessfulHistoryFile`
2. `SaveAsync_WritesFailedHistoryFile`
3. `SaveAsync_CreatesNewFile_ForEveryRun`
4. `SaveAsync_UsesUtcFinishedTimestampInFileName`
5. `SaveAsync_DoesNotOverwrite_WhenFileNameConflicts`
6. `SaveAsync_WritesStructuredAssetCreatedUpdatedSkippedFailedLists`
7. `SaveAsync_WritesStructuredCompanyCategoryAndModelLists`
8. `SaveAsync_WritesDeletedArraysAsEmpty_WhenNoDeleteActionsExist`
9. `SaveAsync_DoesNotPersistSecretsOrRawPayloads`
10. `ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryMissing`
11. `ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryEmpty`
12. `ReadLatestAsync_ReturnsNewestValidSnapshot`
13. `ReadLatestAsync_SkipsMalformedNewestFile_AndReadsNextValidFile`
14. `ReadLatestAsync_ComputesLastSuccessAt_FromHistory`
15. `ReadLatestAsync_ReturnsNull_WhenAllHistoryFilesMalformed`
16. `Constructor_ThrowsArgumentNullException_ForNullOptions`
17. `Constructor_ThrowsArgumentNullException_ForNullLogger`
18. `Constructor_ThrowsArgumentException_ForBlankHistoryDirectory`
19. `SaveAsync_ThrowsArgumentNullException_WhenResultNull`
20. `SaveAsync_HonorsCancellation`
21. `ReadLatestAsync_HonorsCancellation`

## 5. 重点断言

测试重点确认：

- 每次 `SaveAsync` 都写入新的 `SyncResult_*.json`
- 文件名使用 `SyncRunResult.FinishedAt` 转换后的 UTC timestamp
- 同一 UTC tick 冲突时追加短 GUID suffix，不覆盖旧文件
- JSON 顶层包含 `schemaVersion`、`run`、`summary`、resource change sets、`warnings`、`failures`
- `assets` / `companies` / `models` / `manufacturers` / `categories` 都包含 `created`、`updated`、`deleted`、`skipped`、`failed`
- 当前无删除策略下 `deleted` arrays 保留但为空
- `ReadLatestAsync` 从最新有效 history 重建 `SyncStatusSnapshot`
- 最新文件 malformed 时会跳过并读取下一个有效 history
- `LastSuccessAt` 从历史文件扫描得到，不只看最新文件
- history 不持久化 Atera raw JSON、mapped asset notes、API token 哨兵值或 raw payload 字段名
- cancellation 通过 `OperationCanceledException` 传播

## 6. 常见失败原因

- 文件名使用本地时间而不是 UTC，导致 `SaveAsync_UsesUtcFinishedTimestampInFileName` 失败。
- 同一 `FinishedAt` 保存两次时覆盖旧文件，导致冲突测试或每次新文件测试失败。
- JSON 序列化遗漏空 arrays，导致 TrayApp 后续需要特殊 null handling。
- 把 `AteraPullResult.Agents` 或 `SnipeImportBatch.Assets` 整体写入 history，导致 raw payload / secret 哨兵测试失败。
- `ReadLatestAsync` 遇到 malformed JSON 直接抛异常，而不是跳过文件。
- `LastSuccessAt` 只取最新文件，导致最新 run 失败时无法显示之前成功时间。

## 7. 真实 API 安全规则

Status Store 自动化测试必须保持本地、离线、可重复。

不得在 Status Store 测试中：

- 调用真实 Atera API
- 调用真实 Snipe-IT API
- 读取真实 API key 或 token
- 写入 `C:\ProgramData\AteraSnipeSync\History`
- 依赖 WorkerService、TrayApp 或真实 DI host

如未来需要人工检查真实 history 文件展示效果，应由项目 owner 手动运行完整应用，并确保 API key 不打印、不记录、不提交。
