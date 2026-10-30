# WinUI 3 实验结果

验证日期：2026-10-30。仅打开 WorkFor 自身窗口，没有修改 Explorer。

## 构建

```powershell
& .\.tools\dotnet\dotnet.exe build src/WorkFor.UI/WorkFor.UI.csproj
& .\.tools\dotnet\dotnet.exe run --project experiments/ui/WorkFor.UI.ProtocolProbe.csproj
```

已使用项目本地 .NET SDK 10.0.403 构建，0 警告、0 错误。Microsoft.WindowsAppSDK 2.6.4 直接从 NuGet 成功恢复，界面实际启动成功。

实际兼容性发现：.NET SDK 10.0.403 仍拒绝 TFM `net10.0-windows10.0.28000.0`，即便设置 `WindowsSdkPackageVersion`。UI 使用 SDK 支持的 `net10.0-windows10.0.26100.0`，并显式固定最新 Windows SDK .NET Ref `10.0.28000.87`，没有降级 Windows App SDK / .NET 引用依赖。

## 协议验证

协议 probe 使用独立随机管道名，不占用产品会话管道。通过以下行为：

- 读取拆分的长度头和正文，不能依赖单次 Read 返回完整消息。
- 发送 `display.subscribe`，消费 Host 快照。
- 同一 generation 下拒绝重复和倒退 sequence。
- 接受 Host 新 generation 从较小 sequence 重新开始。
- 控制请求 `config.get` 与返回 requestId 关联。
- `rule.catalog` 读取内置时薪 / 月薪目录；候选规则同时带入源文、清单与默认参数，不改动活动配置。
- 日期覆盖、跨午夜时段、币种、任务栏宽度和登录启动选项保留到候选请求。
- `host.shutdown` 在收到停止确认后才关闭 UI；详情保留动态今日 / 本月标题、币种、折算时薪标注与可理解的错误信息。

## 原生 UI 与真实 Host

已启动真实 Host / ScriptHost 后验证：

- 详情显示真实宿主结果，金额从 `¥93.48` 更新为 `¥93.80`，计薪时长同步变化。该数据为内置时薪 16 元规则的实验结果，不是用户工资记录。
- 设置界面成功读取 Host 的规则源文和参数。
- UIAutomation 调用“验证规则”后，出现“验证通过 / 当前修改可应用，现有计算规则尚未改变”。
- 高级清单编辑实际读取 `parametersSchema`；将参数 Schema 根类型从 object 改为 array 后点验证，显示“修改未生效 / Parameters do not satisfy the rule schema.”，恢复原清单后再次验证通过。该操作没有调用应用或改变活动规则。
- 原生导航在收入规则、计薪日历、任务栏、诊断之间往返成功。
- 无暂停、开始、继续或手动金额刷新控件。
- UI 首个进程接收后续启动激活：第二个启动进程退出，首进程内打开详情 / 设置窗口；重复同类型激活复用已有窗口。
- UIAutomation 关闭最后窗口后，进程正常退出。

截图保存在被 Git 忽略的 `out/ui/`：`details-live.png`、`details-live-after.png`、`settings-live.png`、`settings-validated.png`。这些是实际窗口捕获，不是设计图片。

## 接口

默认 pipe 与架构一致；界面只读金额快照，不执行自己的收入计算器。

```text
WorkFor.UI.exe
WorkFor.UI.exe --settings
WorkFor.UI.exe --anchor-rect x,y,width,height
WorkFor.UI.exe --quit
```

锚点坐标为 physical pixels，窗口使用 `DisplayArea.GetFromRect` 选择屏幕并限制到工作区。无锚点时默认主屏右下；尚未验证混合 DPI 多屏的全部组合。

设置支持原生清单 / 参数 JSON 编辑和清单 Schema。规则未连接、错误或请求超时时展示明确状态，不显示伪造收入。候选 `manifest`、`source`、`parameters`、`display`、`calendar` 与 `expectedRevision` 按 Host 控制协议提交。

设置支持内置时薪 / 月薪切换和 `calendar.dateOverrides` 原生 JSON 编辑。选择内置规则只更新草稿；保存任务栏或日历只提交对应页面的修改，不会意外激活其他页面尚未应用的规则。任务栏宽度应用和登录启动由 Host 完成实际系统操作，UI 仅在保存成功后更新活动值。

简单平面参数 Schema 会生成原生 TextBox / NumberBox / ToggleSwitch / ComboBox：使用 title、description、required，提供显式的参数存在性和“使用默认值”操作。显示表单不会自动插入缺失参数或默认工资，未知参数保持原样。参数 JSON 保留为可展开高级输入，与原生控件双向同步；普通输入不重建控件。复杂 Schema、`$ref`、数组、嵌套对象、类型联合及布尔 Schema 回退高级 JSON。

NumberBox 的待提交文字在验证、预览和应用前统一检查：无效数字或非整数被拒绝，清空数字移除对应值，不能把旧工资藏在底层 JSON 后继续保存。必填值缺失由 Host 的 Schema 验证拒绝。

页脚“退出 WorkFor”先调用 Host 的正常退出接口，再关闭全部 WorkFor 窗口。关闭普通设置 / 详情窗口只关闭界面；`--quit` 只退出 UI 实例，供启动器和安装器协调。未运行 UI 时 `--quit` 直接成功返回，不启动新窗口。

第二轮验证仅通过 computer-use JS 做窗口交互；不能输入的桌面会停止 UI 操作，不通过修改桌面安全设置或其他输入方式绕过。源码、协议与自有进程测试不依赖桌面输入能力。

## 开发自有窗口的参数控件测试

```powershell
& .\.tools\dotnet\dotnet.exe build experiments/ui/FormProbe/WorkFor.UI.FormProbe.csproj -c Release
```

运行该实验程序可用 `--result <绝对路径>` 写结果。它在自己的 WinUI 窗口内部设置自身控件属性、检查事件和状态，不使用 UI Automation、SendInput、桌面输入或用户的 Host 管道。23 项检查均通过，结果为 `out/ui/form-probe.json`，运行时为 .NET 10.0.14 x64，包括五种原生控件、输入焦点所依赖的对象引用保持、默认值不自动写入、JSON 双向同步不递归、无效 / 清空数字、required 缺失、多数字字段错误后重试不恢复旧值、复杂 Schema 回退。测试结束自动关闭自身窗口。

## 尚待验证

- P0 任务栏模块真正预留空间后的点击锚定与前台权限。
- 读屏、全套高对比度、跨显示器 DPI 与目标尺寸矩阵。
- 安装后的自包含发布目录和干净机器验证。
- UI 侧错误分支的完整自动化覆盖；当前验证不应当作以上项目已全部通过。
