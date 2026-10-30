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
```

锚点坐标为 physical pixels，窗口使用 `DisplayArea.GetFromRect` 选择屏幕并限制到工作区。无锚点时默认主屏右下；尚未验证混合 DPI 多屏的全部组合。

设置支持原生清单 / 参数 JSON 编辑和清单 Schema。规则未连接、错误或请求超时时展示明确状态，不显示伪造收入。候选 `manifest`、`source`、`parameters`、`display`、`calendar` 与 `expectedRevision` 按 Host 控制协议提交。

## 尚待验证

- P0 任务栏模块真正预留空间后的点击锚定与前台权限。
- 读屏、全套高对比度、跨显示器 DPI 与目标尺寸矩阵。
- 安装后的自包含发布目录和干净机器验证。
- UI 侧错误分支的完整自动化覆盖；当前验证不应当作以上项目已全部通过。
