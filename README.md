# WorkFor：任务栏实时收入显示

仅面向 Windows 的原生桌面程序。任务栏内占用真实横向布局空间，持续显示由用户 JavaScript 规则计算的收入；点击后打开微软 Fluent 风格的详情面板和设置界面。没有开始、暂停、继续或手动刷新控件。

目前交付物是技术设计与脚本契约，尚未实现、编译或验证任务栏注入模块。

- [整体技术架构](docs/技术架构设计.md)：平台限制、进程、任务栏实现、IPC、实时计算、配置、发布和验收。
- [JavaScript 脚本 API](docs/脚本API规范.md)：完整输入输出、金额算法、日历语义、示例、错误与版本策略。
- [依赖与构建方案](docs/依赖与构建.md)：2026-10-30 核实的稳定最新版及版本管理。
- [机器可读依赖基线](docs/dependency-baseline.json)：直接查询官方发布源与 NuGet 的结果，不是已恢复的构建锁文件。
- [TypeScript 类型声明](docs/examples/workfor-api.d.ts)：脚本接口的设计契约。

![已确认的视觉方向](docs/assets/winui3-concept-v2.png)

上图是概念效果，不代表 Windows 提供对应的公开任务栏插件接口。Windows 11 方案需要进程内适配模块；传统 DeskBand 仅作为旧任务栏的独立扩展路线。详见架构文档第一节。
