# Status Store - 单元测试指导手册

Module 5 Status Store production code and automated tests have not been implemented yet.

Per project workflow, this test guide must be completed after `JsonFileSyncStatusStore` and its unit tests exist, so it can document the actual test file, commands, coverage, mocking strategy, latest build/test result, and common failure causes.

The intended implementation now stores every sync run as a separate structured history file named like:

```text
SyncResult_yyyyMMdd_HHmmss_fffffffZ.json
```

The planned tests must verify all-history persistence, UTC file naming, structured resource change lists, malformed-file skipping, and latest snapshot reconstruction.

Planned test cases are currently defined in:

```text
docs/technical-specs/05-StatusStore-技术规格.md
```
