# WorkFor：任务栏实时收入显示

仅面向 Windows 的原生桌面程序。任务栏内占用真实横向布局空间，持续显示由用户 JavaScript 规则计算的收入；点击后打开微软 Fluent 风格的详情面板和设置界面。没有开始、暂停、继续或手动刷新控件。

目前已有可编译的 Windows 11 x64 原型：自带原生加载器 / DLL、独立 Jint 计算进程、每秒更新的后台宿主和 WinUI 3 详情 / 设置。真实任务栏布局与移除恢复已在本机精确版本验证；完整兼容性与安装发布尚未完成。

## 构建与运行

开发命令使用 PowerShell 7。首次运行会将 .NET SDK 10.0.403 放入项目忽略的 `.tools`，不修改系统 SDK。

```powershell
./scripts/bootstrap-dotnet.ps1
./scripts/build.ps1 -PrototypeToolchain -Test
./scripts/run-dev.ps1 -Settings
```

`-PrototypeToolchain` 明确允许使用本机现有 MSVC / CMake 做研究构建；正式基线仍要求当前稳定最新版，默认构建会检查并拒绝旧工具链。原生程序需要本机 MSVC x64 与 Windows SDK。`.NET`、WinUI 和 Jint 已按锁文件恢复当前稳定版本。

只运行后台和设置、不附着任务栏：

```powershell
./scripts/run-dev.ps1 -WithoutTaskbar -Settings
./scripts/stop-dev.ps1
```

任务栏原型严格匹配已验证的 `taskbar.dll` 文件指纹，未知版本会拒绝附着，计算和设置仍可用。当前会话曾加载的研究 DLL 会休眠驻留至 Explorer 自然退出；更新原生构建后优先在下次登录验证，避免覆盖正在映射的文件。

## 文档与实验证据

- [实现与验证进展](docs/实现进展.md)：已实现功能、真实实验、当前限制及后续门禁。

- [整体技术架构](docs/技术架构设计.md)：平台限制、进程、任务栏实现、IPC、实时计算、配置、发布和验收。
- [自带任务栏组件](docs/自带任务栏组件.md)：自有加载器、DLL 引导、生命周期、兼容性、安装升级与故障恢复。
- [JavaScript 脚本 API](docs/脚本API规范.md)：完整输入输出、金额算法、日历语义、示例、错误与版本策略。
- [依赖与构建方案](docs/依赖与构建.md)：2026-10-30 核实的稳定最新版及版本管理。
- [机器可读依赖基线](docs/dependency-baseline.json)：直接查询官方发布源与 NuGet 的结果，不是已恢复的构建锁文件。
- [TypeScript 类型声明](docs/examples/workfor-api.d.ts)：脚本接口的设计契约。
- [原生引导实验](experiments/native-bootstrap/README.md)、[任务栏布局实验](experiments/taskbar/research-notes.md)、[跨进程宿主测试](experiments/host/README.md)、[WinUI 3 验证](experiments/ui/README.md)。

![已确认的视觉方向](docs/assets/winui3-concept-v2.png)

上图是概念效果，不代表 Windows 提供对应的公开任务栏插件接口。Windows 11 采用随安装包交付的 `WorkFor.NativeBridge.exe` 和 `WorkFor.Taskbar.dll`，用户无需另装任务栏框架。传统 DeskBand 仅作为旧任务栏的独立扩展路线。详见架构文档第一节。
