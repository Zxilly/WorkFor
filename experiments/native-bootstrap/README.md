# 自带加载器 P0a 实验

实验日期：2026-10-30。实现源位于 `src/WorkFor.NativeBridge`、`src/WorkFor.Taskbar/Bootstrap.cpp` 与 `src/WorkFor.Native.Contracts`。

## 已完成和已验证

1. 自带加载器发现真实任务栏窗口，记录 Explorer 的路径、PID、线程、创建时间、会话、用户、架构与完整性级别。`--inspect` 只读，不安装钩子。
2. `--self-test` 创建本程序自己的隐藏 ProbeTarget 子进程，对它执行目标线程限定 `WH_GETMESSAGE` DLL 引导。除这个由本次实验创建的子进程外，不向任何进程安装实验钩子。
3. 管道服务端只允许当前用户、拒绝远程、首实例创建；双方核对管道实际对端 PID、映像路径、用户与会话。
4. hello 校验 PID、线程、进程创建时间、原生 ABI 与尝试编号；DLL 确认 PIN 生命周期后加载器才移除引导钩子。
5. `Bootstrapped -> Dormant`，同一个已驻留 DLL 可接受第二次引导重连。测试保留 `layoutAttached=false`；未接入布局适配器时，`native.attach` 明确返回 `LAYOUT_ADAPTER_NOT_CONNECTED`。
6. 错误目标 PID 在安装钩子前被拒绝。
7. 帧拆分、超长长度头、读取截止、取消后 IO 完成、半包、重复 JSON 属性、坏 JSON、非对象和过深 JSON 均有真实命名管道用例。
8. `--stdio` 的私有控制协议已跑通 `bridge.queryState`、`bridge.ensureAttached`、`bridge.detach`、未知消息与 EOF 退出。没有兼容性证明时，ensure 返回 `Unsupported / COMPATIBILITY_NOT_VERIFIED`，不会对 Explorer 执行引导。

脱敏结果见 [p0a-result.json](p0a-result.json)。它反映构建与自有目标实验，不是完整任务栏布局验收。

## 本机实际环境

- Windows 11 25H2 x64，构建 26200.9457。
- 本机实际 Explorer 与工具进程为相同 High 完整性级别（RID 12288），不是原设计假设的 Medium。
- 生产目标身份验证已修正为：同用户、同会话、同完整性级别，允许 Medium / High，拒绝 Low / System，不自行提权。
- 实验使用已安装 MSVC 14.44.35207（VS 2022 17.14.20），`/std:c++20 /MT /W4`，无编译警告。
- 这是可用本机编译器的实验结果。用户要求的最新工具链尚需父任务安装 / 整合，不能把该旧工具链实验冒称为最新构建。
- nlohmann/json 3.12.0 单头来自官方 tag，SHA-256：`aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63`。

## 重现方式

根工程整合后，使用父任务的 native 构建命令产生相邻的：

```text
WorkFor.NativeBridge.exe
WorkFor.Taskbar.dll
```

执行：

```powershell
.\WorkFor.NativeBridge.exe --inspect
.\WorkFor.NativeBridge.exe --self-test
```

`--self-test` 成功退出码为 0，顶层 `passed=true`、`productionExplorerHooked=false`、`layoutAttached=false`，两个 `attempts` 都包含 Bootstrapped / Dormant 结果。自有子进程正常退出；仅在它不响应 WM_CLOSE 时终止本次实验创建的子进程，代码没有终止 Explorer 路径。

手工编译所需文件和库：

```text
Taskbar DLL:
  src/WorkFor.Taskbar/Bootstrap.cpp

NativeBridge:
  src/WorkFor.NativeBridge/main.cpp
  src/WorkFor.NativeBridge/TargetIdentity.cpp
  src/WorkFor.NativeBridge/BootstrapClient.cpp
  src/WorkFor.NativeBridge/ProtocolSelfTest.cpp
  src/WorkFor.NativeBridge/DiscoveryWatcher.cpp
  src/WorkFor.NativeBridge/ControlStdio.cpp

Defines: UNICODE, _UNICODE, NOMINMAX
Include: 解压 nlohmann/json 3.12.0 后的 include 根目录
Libraries: user32.lib advapi32.lib bcrypt.lib
```

如果将 Adapter.cpp / Compatibility.cpp 同时编入 DLL，需要按任务栏实验提供的额外系统库链接；本实验的引导测试本身不需要创建 XAML 对象。

## stdio 约定

使用重定向的 stdin / stdout，消息是 4 字节 little-endian 正文长度和 UTF-8 JSON。每帧最多 16KiB；stdout 禁止日志，错误诊断只在 stderr。

```json
{
  "protocolVersion": 1,
  "type": "bridge.ensureAttached",
  "requestId": "1"
}
```

响应复用 requestId，type 为 `bridge.status`：

```json
{
  "protocolVersion": 1,
  "type": "bridge.status",
  "requestId": "1",
  "payload": {
    "state": "Unsupported",
    "nativeAbiVersion": 1,
    "code": "COMPATIBILITY_NOT_VERIFIED",
    "layoutAttached": false,
    "productionExplorerHooked": false,
    "targets": []
  }
}
```

实际 targets 包含只读发现结果，示例省略机器身份数据。`bridge.detach` 只改变控制意图为 Dormant，目前生产布局从未附着，因此不虚构一次物理卸载。

## 只读 Explorer 进程内实验入口

实现了额外的显式命令，供主任务协调后执行：

```powershell
.\WorkFor.NativeBridge.exe --probe-explorer
```

此入口在加载前验证系统 Explorer 路径、同 SID / 会话 / 完整性级别、实际已加载的系统 taskbar.dll 路径和 `Compatibility.h` 共享的精确研究 SHA-256。DLL 在任务栏线程只调用 `AdapterInspect`，按有界 ASCII JSON 分片回传树结构，然后进入 Dormant，不调用 `AdapterAttach`。这仍会把 DLL PIN 到 Explorer 退出，并非完全没有进程状态改变的外部读取。

该入口已在精确指纹门禁下完成真实系统执行与树审查，成功找到根对象。默认 `--inspect` 和 `--stdio` 都不会安装目标线程钩子。

## 当前限制

- 真实任务栏已通过下述有时限实验；完整拥挤 / 溢出、多显示器与长期运行验证尚未完成，不能声称普遍生产兼容。
- 自有 ProbeTarget 为纯 Win32 隐藏窗口，不等于 Windows 11 XAML 任务栏。
- 实验未关闭或重启 Explorer；已用精确指纹门禁执行只读钩子和有时限真实布局实验。
- DLL 被 PIN 到目标进程退出；没有物理热卸载承诺。
- 用户脚本与工资参数从不传入此引导实验。
- 原生控制进程的持续状态推送、持久化异常预算和正式安装签名验证仍需集成；目前 stdio query 返回实时枚举状态。

## 后续 Live 实验入口（交由主任务运行）

另构建固定 basename `WorkFor.Taskbar.Live.dll` 到新的 `out/native-live` 目录，避免覆盖已被 Explorer PIN 的第一版只读 DLL。Live 与 baseline 源码相同，但仅 Live basename 允许调用真实布局适配器。

```powershell
.\WorkFor.NativeBridge.exe --attach-explorer --duration 10
```

加载前继续验证精确研究指纹，并以当前用户 / 会话命名的 `WorkFor.NativeActive` mutex 阻止第二个活动布局组件。基线只读 DLL 没有布局 / 事件租约，不占这个活动标识。执行期间每秒续租，10 秒后自动清理并等待 Dormant。若 UI 逆向操作未能证明完成，则保留活动标识和代码，报告失败，不伪装 Dormant。

Live 模块直接订阅 `workfor-display-v1-{SID}-{session}`，验证 Host 对端实际 PID、用户、会话、完整性与 `WorkFor.Host.exe` 名称，使用 Host 的金额字符串和状态。每个成功投递到真实适配器的快照增加 `displayUpdates`，该计数包含在 lease / detach 结果中。首次没有快照显示 `--` / 连接中，不显示固定演示收入。

UI 操作通过目标任务栏线程的私有控制窗口派发；worker 通过 ticket / shared ownership 等待有界操作。即使等待超时，仍在 UI 线程执行的参数和事件不会被释放。断开控制管道时，worker 停止显示线程并尝试逆向清理。

持续模式供 Host 作为子进程启动：

```text
WorkFor.NativeBridge.exe --run-explorer
```

必须重定向 stdin / stdout。Host 仅保持 stdin 打开即可，关闭 stdin / EOF 触发自动 detach。stdout 为 JSON 行 `bridge.liveState`，其 payload 具有 state、layoutAttached、displayUpdates；每秒一行。停止成功后输出 `bridge.liveStopped`。此模式不与 `--stdio` 的二进制控制帧混用。

### 真实运行结果

2026-10-30 集成实验已执行 10 秒 Live 与第二次现有模块观测，完成自动分离，无需重启 Explorer。脱敏摘要见 [live-layout-result.json](live-layout-result.json)：

- 实际收入容器位于原生根 Grid 第 1 列，宽 260 DIP。
- 系统托盘位于第 2 列，宽 384.667 DIP。
- 应用按钮 repeater 的实际可用宽从 2560 DIP 缩短为 1915.33 DIP。
- 后台 Host 的快照被原生显示线程消费并更新 12 次。
- 分离后收入容器消失、托盘恢复第 0 列、应用区域恢复 2560 DIP，`cleanupConfirmed=true`。

这验证了原生布局空间预留和逆向恢复，不是覆盖窗口。旧只读观察模块未包含全部绝对坐标字段，因此不能由这一次数据单独证明所有拥挤情形无覆盖。

### 点击入口源码与验证范围

后续源码新增了原生 Button / AutomationInvoke 和异步 `ui.openDetails` 投递，坐标为物理屏幕矩形。UI 回调只保存最新激活事件，显示 IO worker 为唯一写入者；先向已连接 Host 授予前台权限，再由 Host 启动或激活界面。

自有 XAML 实验已通过一次真实 AutomationInvoke 触发、有效 rect / DPI 以及 detach 后不再触发事件，详见任务栏布局实验。当前 Explorer 中 PIN 的 Live 版本早于此点击功能，未在现有会话重建 / 覆盖 DLL，因此实际任务栏点击到完整详情界面的链路仍待新版本会话验证。正式安装签名清单仍需实施。
