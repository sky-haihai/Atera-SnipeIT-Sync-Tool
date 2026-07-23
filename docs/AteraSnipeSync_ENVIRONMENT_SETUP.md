# Atera to Snipe-IT Sync System - 环境搭建指导文档

> 用途：在一个完全空的 repo 中，搭建本项目的基础开发环境和项目骨架。  
> 目标读者：AI coding agent / 开发者。  
> 范围：只做环境和项目初始化，不实现业务逻辑。  
> 业务模块职责请参考总策划案。  
> 后续每个模块继续按以下流程生成：
>
> 1. `功能职责.md`
> 2. `技术规格.md`
> 3. `生成代码（包括测试）`
> 4. `单元测试指导手册.md`

---

## 1. 环境目标

本阶段完成后，repo 应该具备：

```text
- .NET 10 LTS 开发环境
- Git repo 基础结构
- Solution 文件
- Core project
- Worker Service project
- Tray App project
- Tests project
- 基础配置文件模板
- 基础目录结构
- 可运行 build
- 可运行空测试
```

本阶段不实现：

```text
- Atera API 调用
- Snipe-IT API 调用
- Mapping 逻辑
- Tray App 真实 UI
- Windows Service 安装器
- Notification 实现
```

---

## 2. 推荐开发环境

### 2.1 操作系统

开发机推荐：

```text
Windows 10 / 11 x64
```

目标运行环境：

```text
Windows Server 2016 x64
Windows Server 2019 x64
Windows Server 2022 x64
Windows Server 2025 x64
```

### 2.2 必装工具

```text
- .NET 10 SDK
- Git
- Visual Studio 2022 或 VS Code / Rider
```

推荐使用 Visual Studio 2022，因为本项目包含：

```text
- Worker Service
- WinForms Tray App
- Unit Tests
```

### 2.3 Visual Studio 推荐 workload

如果使用 Visual Studio，建议安装：

```text
- .NET desktop development
- ASP.NET and web development
```

说明：

```text
- Tray App 需要 Windows desktop / WinForms 支持
- Worker Service 模板可能依赖 ASP.NET and web development workload 显示
```

---

## 3. 检查开发环境

打开 PowerShell，执行：

```powershell
dotnet --info
```

需要确认：

```text
- 已安装 .NET SDK 10.x
- OS 是 win-x64
```

检查 Git：

```powershell
git --version
```

检查可用模板：

```powershell
dotnet new list
```

需要能看到类似：

```text
classlib
worker
winforms
xunit
sln
```

如果看不到 `worker` 或 `winforms`，先检查 .NET SDK 和 Visual Studio workload 是否安装完整。

---

## 4. 创建 Repo

在目标目录执行：

```powershell
mkdir AteraSnipeSync
cd AteraSnipeSync
git init
```

建议初始目录：

```text
AteraSnipeSync/
```

---

## 5. 创建基础目录结构

执行：

```powershell
mkdir src
mkdir tests
mkdir docs
mkdir docs\module-plans
mkdir docs\technical-specs
mkdir docs\test-guides
mkdir samples
mkdir samples\configs
mkdir samples\fixtures
mkdir tools
```

目标结构：

```text
AteraSnipeSync/
│
├─ src/
├─ tests/
├─ docs/
│  ├─ module-plans/
│  ├─ technical-specs/
│  └─ test-guides/
├─ samples/
│  ├─ configs/
│  └─ fixtures/
└─ tools/
```

目录职责：

```text
src/
→ production code

tests/
→ unit tests and integration-style tests

docs/module-plans/
→ 每个模块的 功能职责.md

docs/technical-specs/
→ 每个模块的 技术规格.md

docs/test-guides/
→ 每个模块的 单元测试指导手册.md

samples/configs/
→ 示例配置，不放真实 token

samples/fixtures/
→ 测试用假 Atera / Snipe-IT JSON

tools/
→ 开发辅助脚本
```

---

## 6. 创建 Solution

执行：

```powershell
dotnet new sln -n AteraSnipeSync
```

生成：

```text
AteraSnipeSync.sln
```

---

## 7. 创建 Projects

### 7.1 Core Project

Core 存放共享接口、数据模型、纯逻辑模块。

```powershell
dotnet new classlib `
  -n AteraSnipeSync.Core `
  -o src\AteraSnipeSync.Core `
  -f net10.0
```

---

### 7.2 Worker Service Project

Worker Service 负责定时运行同步流程。

```powershell
dotnet new worker `
  -n AteraSnipeSync.WorkerService `
  -o src\AteraSnipeSync.WorkerService `
  -f net10.0
```

---

### 7.3 Tray App Project

Tray App 负责本地配置和状态查看。

```powershell
dotnet new winforms `
  -n AteraSnipeSync.TrayApp `
  -o src\AteraSnipeSync.TrayApp `
  -f net10.0-windows
```

如果 `dotnet new winforms` 不可用，先安装 Visual Studio `.NET desktop development` workload。

---

### 7.4 Tests Project

第一版测试项目使用 xUnit。

```powershell
dotnet new xunit `
  -n AteraSnipeSync.Tests `
  -o tests\AteraSnipeSync.Tests `
  -f net10.0
```

---

## 8. 添加 Projects 到 Solution

执行：

```powershell
dotnet sln add src\AteraSnipeSync.Core\AteraSnipeSync.Core.csproj
dotnet sln add src\AteraSnipeSync.WorkerService\AteraSnipeSync.WorkerService.csproj
dotnet sln add src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj
dotnet sln add tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj
```

检查：

```powershell
dotnet sln list
```

---

## 9. 添加 Project References

Worker Service 需要引用 Core：

```powershell
dotnet add src\AteraSnipeSync.WorkerService\AteraSnipeSync.WorkerService.csproj `
  reference src\AteraSnipeSync.Core\AteraSnipeSync.Core.csproj
```

Tray App 需要引用 Core：

```powershell
dotnet add src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj `
  reference src\AteraSnipeSync.Core\AteraSnipeSync.Core.csproj
```

Tests 需要引用 Core：

```powershell
dotnet add tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj `
  reference src\AteraSnipeSync.Core\AteraSnipeSync.Core.csproj
```

后续如果要测试 Worker 或 Tray，可以再加引用：

```powershell
dotnet add tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj `
  reference src\AteraSnipeSync.WorkerService\AteraSnipeSync.WorkerService.csproj
```

第一版建议先只测 Core，减少依赖复杂度。

---

## 10. 添加基础 NuGet Packages

### 10.1 Worker Service Windows Service 支持

让 Worker 可以作为 Windows Service 运行：

```powershell
dotnet add src\AteraSnipeSync.WorkerService\AteraSnipeSync.WorkerService.csproj `
  package Microsoft.Extensions.Hosting.WindowsServices
```

### 10.2 HTTP Client / Configuration / Logging

Worker Service 模板通常已经包含基础 hosting、configuration、logging。

第一版可以暂时不额外添加包。

后续实现 API client 时，如果需要 retry policy，可以再考虑添加：

```text
Polly 或 Microsoft.Extensions.Http.Resilience
```

但环境搭建阶段不要提前引入不必要依赖。

### 10.3 Testing Packages

xUnit 模板一般已经包含：

```text
xunit
xunit.runner.visualstudio
Microsoft.NET.Test.Sdk
```

检查测试项目 `.csproj`，确认存在这些 package references。

---

## 11. 创建基础文档文件

执行：

```powershell
New-Item README.md -ItemType File
New-Item docs\PROJECT_PLAN.md -ItemType File
New-Item docs\ENVIRONMENT_SETUP.md -ItemType File
New-Item docs\module-plans\README.md -ItemType File
New-Item docs\technical-specs\README.md -ItemType File
New-Item docs\test-guides\README.md -ItemType File
```

建议内容：

```text
README.md
→ 项目概述、如何 build、如何 test

docs/PROJECT_PLAN.md
→ 简化总策划案

docs/ENVIRONMENT_SETUP.md
→ 本文档

docs/module-plans/
→ 每个模块的功能职责

docs/technical-specs/
→ 每个模块的技术规格

docs/test-guides/
→ 每个模块的测试指导
```

---

## 12. 创建示例配置文件

创建：

```powershell
New-Item samples\configs\appsettings.local.example.json -ItemType File
```

写入：

```json
{
  "Atera": {
    "ApiKey": "REPLACE_WITH_ATERA_API_KEY"
  },
  "SnipeIt": {
    "BaseUrl": "https://snipe.example.com",
    "ApiToken": "REPLACE_WITH_SNIPE_API_TOKEN",
    "DefaultStatusId": 2
  },
  "Sync": {
    "IntervalMinutes": 30
  },
  "Notifications": {
    "Enabled": false,
    "OnEvents": [
      "SyncFailed"
    ],
    "EmailTo": null,
    "WebhookUrl": null
  }
}
```

注意：

```text
这个 example 文件可以提交到 Git。
真实 appsettings.local.json 不允许提交。
```

---

## 13. 创建本地运行配置路径

本项目运行时建议使用：

```text
C:\ProgramData\AteraSnipeSync\
```

本地开发时可以先手动创建：

```powershell
New-Item "C:\ProgramData\AteraSnipeSync" -ItemType Directory -Force
New-Item "C:\ProgramData\AteraSnipeSync\Logs" -ItemType Directory -Force
```

复制示例配置：

```powershell
Copy-Item samples\configs\appsettings.local.example.json `
  "C:\ProgramData\AteraSnipeSync\appsettings.local.json"
```

然后手动编辑：

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

替换真实：

```text
Atera API Key
Snipe-IT Base URL
Snipe-IT API Token
Default Status ID
```

---

## 14. 创建 .gitignore

创建：

```powershell
New-Item .gitignore -ItemType File
```

写入：

```gitignore
# Build outputs
bin/
obj/
.vs/
.vscode/
.idea/

# User-specific files
*.user
*.suo
*.rsuser

# Logs
*.log
Logs/
logs/

# Local secrets/config
appsettings.local.json
*.local.json
.env

# Publish outputs
publish/
artifacts/

# Test results
TestResults/
coverage/
*.trx
*.coverage
*.coveragexml

# OS files
.DS_Store
Thumbs.db
```

注意：

```text
samples/configs/appsettings.local.example.json 可以提交。
真实 appsettings.local.json 不可以提交。
```

---

## 15. 创建基础 README

`README.md` 建议先写：

```markdown
# AteraSnipeSync

A modular .NET sync system that imports Atera managed devices into Snipe-IT.

## Projects

- `AteraSnipeSync.Core`
- `AteraSnipeSync.WorkerService`
- `AteraSnipeSync.TrayApp`
- `AteraSnipeSync.Tests`

## Build

```powershell
dotnet build
```

## Test

```powershell
dotnet test
```

## Local Config

Copy:

```text
samples/configs/appsettings.local.example.json
```

to:

```text
C:\\ProgramData\\AteraSnipeSync\\appsettings.local.json
```

Do not commit real API keys or tokens.
```

---

## 16. 第一次 Build

执行：

```powershell
dotnet restore
dotnet build
```

验收：

```text
Build succeeded.
0 Error(s)
```

如果失败，先不要写业务逻辑，先修复 project references / target framework / SDK 问题。

---

## 17. 第一次 Test

执行：

```powershell
dotnet test
```

验收：

```text
Test run successful.
```

此时测试可能只有模板自带测试。

---

## 18. 创建基础 Fixture 文件

创建：

```powershell
New-Item samples\fixtures\atera-agents.sample.json -ItemType File
New-Item samples\fixtures\atera-customers.sample.json -ItemType File
New-Item samples\fixtures\snipe-assets.sample.json -ItemType File
```

先写空数组即可：

```json
[]
```

后续 Module 1 / Module 2 / Module 3 的测试会逐步填入模拟数据。

---

## 19. 创建模块文档占位文件

执行：

```powershell
New-Item docs\module-plans\01-AteraPull-功能职责.md -ItemType File
New-Item docs\module-plans\02-Reconstruction-功能职责.md -ItemType File
New-Item docs\module-plans\03-SnipeImport-功能职责.md -ItemType File
New-Item docs\module-plans\04-SyncOrchestrator-功能职责.md -ItemType File
New-Item docs\module-plans\05-StatusStore-功能职责.md -ItemType File
New-Item docs\module-plans\06-Notification-功能职责.md -ItemType File
New-Item docs\module-plans\07-WorkerScheduler-功能职责.md -ItemType File
New-Item docs\module-plans\08-TrayApp-功能职责.md -ItemType File
```

技术规格占位：

```powershell
New-Item docs\technical-specs\01-AteraPull-技术规格.md -ItemType File
New-Item docs\technical-specs\02-Reconstruction-技术规格.md -ItemType File
New-Item docs\technical-specs\03-SnipeImport-技术规格.md -ItemType File
New-Item docs\technical-specs\04-SyncOrchestrator-技术规格.md -ItemType File
New-Item docs\technical-specs\05-StatusStore-技术规格.md -ItemType File
New-Item docs\technical-specs\06-Notification-技术规格.md -ItemType File
New-Item docs\technical-specs\07-WorkerScheduler-技术规格.md -ItemType File
New-Item docs\technical-specs\08-TrayApp-技术规格.md -ItemType File
```

测试手册占位：

```powershell
New-Item docs\test-guides\01-AteraPull-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\02-Reconstruction-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\03-SnipeImport-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\04-SyncOrchestrator-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\05-StatusStore-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\06-Notification-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\07-WorkerScheduler-单元测试指导手册.md -ItemType File
New-Item docs\test-guides\08-TrayApp-单元测试指导手册.md -ItemType File
```

---

## 20. 推荐开发顺序

环境搭好后，不要马上写所有代码。

按照这个顺序推进：

```text
1. 共享接口和数据模型
2. Reconstruction Module
3. Atera Pull Module
4. Snipe-IT Import Module
5. Sync Orchestrator
6. Status Store
7. Notification Stub
8. Worker Scheduler
9. Basic Tray App
10. End-to-End Dry Run
```

原因：

```text
Reconstruction Module 是纯逻辑，最容易测试。
Atera 和 Snipe-IT 是外部 API，应该在 contract 稳定后再写。
Tray App 是外壳，应该最后做。
```

---

## 21. AI Agent 工作规则

每次让 AI agent 生成模块时，必须指定当前阶段。

### 21.1 Phase A - 功能职责.md

AI agent 只写：

```text
- 模块目标
- 输入
- 输出
- 对外接口
- 成功条件
- 失败条件
- 不负责的事情
- 扩展点
```

不写具体代码。

---

### 21.2 Phase B - 技术规格.md

AI agent 写：

```text
- class/interface 设计
- method signature
- data models
- error handling
- logging requirement
- config requirement
- test cases
```

不直接开始写 production code。

---

### 21.3 Phase C - 生成代码和测试

AI agent 写：

```text
- production code
- unit tests
- mocked dependencies
- sample fixture/config if needed
```

要求：

```text
dotnet build 通过
dotnet test 通过
```

---

### 21.4 Phase D - 单元测试指导手册.md

AI agent 写：

```text
- 如何运行测试
- 每个测试验证什么
- 如何添加新测试
- 如何 mock 外部依赖
- 常见失败原因
```

---

## 22. 本阶段完成标准

环境搭建阶段完成后，需要满足：

```text
[ ] git repo 已初始化
[ ] solution 已创建
[ ] Core project 已创建
[ ] Worker Service project 已创建
[ ] Tray App project 已创建
[ ] Tests project 已创建
[ ] project references 正确
[ ] .gitignore 已创建
[ ] 示例配置文件已创建
[ ] docs 目录已创建
[ ] module plan/spec/test guide 占位文件已创建
[ ] samples fixtures 已创建
[ ] dotnet restore 成功
[ ] dotnet build 成功
[ ] dotnet test 成功
```

---

## 23. 常见问题

### 23.1 `dotnet new worker` 找不到

可能原因：

```text
- .NET SDK 没装完整
- Visual Studio workload 不完整
```

处理：

```text
- 确认 dotnet --info
- 安装 .NET 10 SDK
- Visual Studio Installer 添加 ASP.NET and web development workload
```

---

### 23.2 `dotnet new winforms` 找不到

可能原因：

```text
- 没有安装 Windows desktop workload
```

处理：

```text
Visual Studio Installer → Modify → .NET desktop development
```

---

### 23.3 Build 报 target framework 不支持

检查：

```text
- 是否安装 .NET 10 SDK
- Core/Worker/Tests 是否 target net10.0
- Tray 是否 target net10.0-windows
```

---

### 23.4 真实 API Key 被 Git 追踪

立即处理：

```powershell
git rm --cached path\to\appsettings.local.json
```

并确认 `.gitignore` 包含：

```gitignore
appsettings.local.json
*.local.json
.env
```

如果已经 push 到远程，需要轮换 API key / token。

---

## 24. 下一步

完成本文档后，进入第一个模块文档：

```text
docs/module-plans/00-SharedContracts-功能职责.md
```

建议先生成 Shared Contracts，而不是直接生成 Atera Pull。

Shared Contracts 应包含：

```text
- shared request/result models
- warning model
- failure model
- import action model
- module interface definitions
```

然后再进入：

```text
01-AteraPull-功能职责.md
02-Reconstruction-功能职责.md
03-SnipeImport-功能职责.md
```
