# Windows 11 任务栏适配实验记录

日期：2026-10-30。此目录为可复现实验与原型证据，不等于生产兼容性声明。

## 已验证的只读证据

- 本机真实任务栏归属 `Shell_TrayWnd`，其 `TaskbandHWND` 属性指向 TaskBand 窗口。
- 实际载入的 `C:\Windows\System32\Taskbar.dll` 文件版本为 `10.0.10011.16384`。
- SHA-256：`5dceea036939acb4e43b801a7a63530f328edad164741e6529d3d32e9b1c5866`。
- CodeView RSDS：`Taskbar.pdb`，GUID `483327DB5FAC66CA1D844F9D8F5054CB`，age `1`。
- 已从微软符号服务器下载精确 PDB，使用本地 Windows SDK DbgHelp，在自己的 Python 进程内枚举符号。

| 符号 | RVA | 用途 |
|---|---|---|
| `CTaskBand::GetTaskbarHost` | `0x1097a0` | x64 C++ ABI 隐藏返回存储，取得 shared_ptr 对象 / control block |
| `TaskbarHost::FrameHeight` | `0x8aefc` | 反汇编确认 Host 内的 FrameworkElement 成员偏移为 `0x10` |
| `std::_Ref_count_base::_Decref` | `0x27ca0` | 释放原模块自己创建的 shared_ptr 引用，不用本项目 CRT 猜测内部控制块 |
| `CTaskBand` 的 `ITaskListWndSite` vtable | `0x1cbf50` | 精确判断 TaskBand 对应接口子对象 |

对目标进程仅作读取，确认 TaskBand 指针 `+0x50` 的 vtable 与精确符号匹配。读取不调用目标函数、不写目标内存。

`Compatibility.cpp` 对 SHA、三段前导指令与 site vtable 全部校验，且要求在目标进程的任务栏 UI 线程调用。仅当前精确文件指纹可用；未知文件不猜偏移。

## 复现符号研究

```powershell
python -X utf8 experiments/taskbar/research-symbols.py --download-pdb
```

需要 x64 Python 与 Windows SDK x64 DbgHelp。PDB、生成的 symbol-evidence.json 放在 Git 忽略的 `out/taskbar-research/`；符号由 PE 中的精确 RSDS 标识匹配，不启用 DbgHelp 的任意版本装载选项。

该脚本只在自己的进程加载 DbgHelp 并解析磁盘文件，不注入 Explorer。首次运行需联网下载微软公开符号，正常应用运行不依赖在线符号服务。

## 原创布局算法与自有 XAML 实验

`adapter-layout-test.cpp` 在自己的 STA 进程调用系统 `WindowsXamlManager`，创建本项目自有 Grid。测试复用 `AdapterAttachToRoot`，不是重新实现一个只与自身断言匹配的模拟算法。

已执行的实验包括：

1. 两列 Grid 中插入 260 DIP 自有列，应用区域实际从 680 DIP 缩至 420 DIP，托盘保持 120 DIP。
2. 窄宽约束下应用区域参与测量，未使用 topmost / SetParent / 覆盖窗口。
3. 文本更新、移除恢复自有列和原始邻接索引。
4. 如果第三方改变原先被移动的列索引，移除返回冲突而非覆盖其修改。
5. 模拟真实只读树的隐式外层 Grid：Frame 与 Tray 为同父子项，Frame 内有 TaskbarFrameRepeater。
6. 插入 `[star, 260 DIP, auto]` 三列，Frame 跨三列维持整幅背景，Tray 放在第三列，自有区域放在中间；只约束应用 repeater 的实际宽度。
7. 外层宽 800 时 Frame 仍宽 800，repeater 宽 420；外层改为 600 后下一次布局 / 更新使 repeater 宽 220。
8. detach 恢复原有零显式列、Frame span、Tray column、repeater 的本地 Width 与 HorizontalAlignment。
9. 源码后续版本将收入区域设为可 Tab 聚焦的原生 Button，具备可访问名称和 UI 线程激活回调；已驻留的首轮 Live 原型仍为 Grid，不具备该点击入口。
10. 自有隐藏 Win32 窗口内创建真实 DesktopWindowXamlSource，`ButtonAutomationPeer` 的 `IInvokeProvider.Invoke()` 触发系统 Button Click，收到一次激活回调，返回非空屏幕锚点与 DPI；移除后再次 Invoke，不再发送激活事件。此实验确实执行系统控件事件，不是只验证消息 JSON。

最近一次实验输出：

```json
{"status":"passed","reservedDip":260,"beforeAppsDip":680,"afterAppsDip":420,"activationEvents":1,"checks":["real-column","constrained-layout","update","detach","conflict-refusal","implicit-root","full-background","resize","native-button-accessibility","system-button-invoke","physical-anchor","event-revoked"]}
```

实验使用本机已有 MSVC 14.44、Windows SDK 26100，不能据此声称文档中最新编译链已验证。当前源可以合入根 CMake，额外链接 `windowsapp.lib`、`runtimeobject.lib`、`bcrypt.lib`、`version.lib`、`user32.lib`。

## 真实进程分阶段验证

root 代理已协调一次只读 `--probe-explorer`，结果存于 `.local/explorer-tree-probe.json`。观察到顶层 Grid 无显式列，直接子项为 `Taskbar.TaskbarFrame` 与 `SystemTray.SystemTrayFrame`；实际 RootGrid 在 TaskbarFrame 内，不能套用“RootGrid 直接包含 Tray”的假设。

该只读实验确认 `ResolveCurrentTaskbarRoot` 可成功返回当前既有树，并成功逻辑分离为 Dormant。它没有证明布局原型已满足拥挤 / 溢出 / 多屏 / 升级稳定性。

随后已统一运行 10 秒的真实可撤销布局实验，并由另一个已驻留只读模块在附着 / 移除阶段分别读取同一任务栏树。脱敏结构证据见 [evidence-p0.json](evidence-p0.json)：

| 检查项 | 附着时 | 移除后 |
|---|---|---|
| 顶层显式列 / 子项 | 3 / 3 | 恢复 0 / 2 |
| 收入区域 | 中间列，宽 260 DIP | 已移除 |
| 应用 repeater | 1915.33 DIP | 恢复 2560 DIP |
| 系统托盘 | 第 2 列，384.667 DIP | 恢复原第 0 列，宽度不变 |
| Frame / 背景 | 宽 2560 DIP，Frame 跨 3 列 | 宽度不变，Frame 恢复跨 1 列 |
| Host 显示更新 | 12 次有效更新 | 逻辑状态 Dormant |

记录 `cleanupConfirmed=true`，实验结束后收入区域不存在，原测量宽度与列结构恢复。该证据不包含 SID、PID、线程 ID、窗口句柄、地址或控制连接代次。

真实实验使用的 Live DLL 是较早的 Grid 容器版本，未包含随后源码新增的 Button 激活入口；后者只完成自有 XAML 测试，不把它算入此次真实验证。此次没有重启 Explorer，PIN 的原型代码仍驻留到 Explorer 自然退出，但未保留活动布局。

当前 `AdapterAttach` 已验证在精确本机版本上能够为金额区域获得真实布局空间；拥挤 / 溢出 / 多屏 / 全部 DPI / 升级仍待资格测试。未通过完整资格测试前不得把当前 fingerprint 当作广泛生产兼容承诺。

## 源资料与许可边界

- [微软 C++/WinRT](https://github.com/microsoft/cppwinrt)：本项目使用 Windows SDK 提供的投影与系统接口。
- [原生 Grid](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.grid)：布局实验使用原生列与子项，不使用视觉覆盖。
- [DbgHelp SymEnumSymbols](https://learn.microsoft.com/en-us/windows/win32/api/dbghelp/nf-dbghelp-symenumsymbolsw)：符号研究由本项目标准库 Python 脚本调用。
- [Taskbar Folder Menus 上游源码](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-folder-menus.wh.cpp)：用于理解私有对象路线；该仓库默认 GPLv3，未复制其实现到本项目。
- [XAML diagnostics demo](https://gist.github.com/m417z/8741e52d8eaad67b47ee365a20070bf8)：另一条 TAP/观察器路线，作者明确 GPLv3 且基于 TranslucentTB；本轮未复制或链接该实现。

本项目 resolver、Grid lease、还原冲突处理、符号脚本均独立编写；公开符号名称、当前模块偏移与反汇编字节是兼容研究数据，不是复用上游运行框架。
