# WorkFor

Windows 11 原生任务栏实时收入显示。自带加载器，在任务栏里占用真正的横向区域，每秒按自定义 JavaScript 规则重新计算收入；点击可打开 WinUI 3 详情与设置。

当前为 x64 原型，仅对已验证的精确任务栏文件版本开放嵌入能力。没有暂停按钮；程序退出或睡眠后，按绝对时刻重算。

## 功能

- 内置时薪 / 月薪规则，自定义 JavaScript、参数和 Draft 2020-12 Schema。
- 计薪工作日、午休、日期覆盖、跨午夜与时区。
- 任务栏宽度即时更新，原生浅深主题、字体缩放、窄空间折叠。
- 规则草稿、预览与原子应用；配置备份恢复、成功快照、登录启动。
- 自带原生启动器、自包含便携包与当前用户安装器，无需 Windhawk、SDK 或 PowerShell 才能运行。

## 运行与安装

成功的 [Windows workflow](https://github.com/Zxilly/WorkFor/actions/workflows/windows-build.yml) 会提供未签名的便携 zip 和 Setup.exe。解压便携包后运行 `WorkFor.exe`；或使用当前用户安装器。参数：

```text
WorkFor.exe --settings     打开设置
WorkFor.exe --background   仅启动后台与任务栏
WorkFor.exe --quit         正常退出并移除显示
WorkFor.exe --verify       校验发行文件，不启动应用
```

任务栏 DLL 为保守的 PIN 生命周期，逻辑移除后代码仍驻留至 Explorer 自然退出。安装器遇仍映射的本产品文件会要求注销后重试，不会强制结束 Explorer。工资 / 规则存放在独立的当前用户数据目录，卸载保留该目录。

## 开发

构建脚本使用 PowerShell 7，SDK / CMake 引导放在 Git 忽略的 `.tools`，不替换系统开发环境。

```powershell
./scripts/bootstrap-dotnet.ps1
./scripts/build.ps1 -PrototypeToolchain -Test
./scripts/run-dev.ps1 -Settings
./scripts/stop-dev.ps1
```

`-PrototypeToolchain` 明确允许当前可用的 MSVC / Windows SDK，产物会记录该状态；省略时要求文档的最新稳定原生基线。只运行计算与界面可用 `run-dev.ps1 -WithoutTaskbar -Settings`。

发布自包含包与安装器：

```powershell
./scripts/build.ps1 -Configuration Release -PrototypeToolchain -Test
./scripts/package.ps1 -Configuration Release -PrototypeToolchain -Setup
```

## 验证与文档

Debug / Release 的 **148 项托管测试、14 组原生测试通过**。本机真实任务栏验证了 180 / 260 / 360 DIP 空间预留、每秒更新和移除恢复；安装 / 卸载往返也已验证。物理点击和完整跨版本兼容矩阵仍需单独验证。

- [实现与验证进展](docs/实现进展.md)
- [整体技术架构](docs/技术架构设计.md)
- [自带任务栏组件](docs/自带任务栏组件.md)
- [JavaScript API](docs/脚本API规范.md) 与 [类型声明](sdk/workfor-api.d.ts)
- [依赖与构建](docs/依赖与构建.md)、[Schema 校验器迁移](docs/schema-migration.md)
- [发布与安装](installer/README.md)
- [原生引导](experiments/native-bootstrap/README.md)、[布局研究](experiments/taskbar/research-notes.md)、[补充验证](experiments/validation/README.md)、[Host](experiments/host/README.md)、[WinUI](experiments/ui/README.md)

## 视觉方向

以下为早期概念图，真实界面使用系统原生控件。

![WinUI 3 风格概念](docs/assets/winui3-concept-v2.png)

## 许可证

项目采用 [MIT](LICENSE)。运行时及第三方库各自的许可证随发行包保留；项目 MIT 不替代依赖许可。
