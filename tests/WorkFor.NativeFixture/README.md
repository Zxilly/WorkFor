# NativeSession 受控协议进程

此程序是测试夹具，AssemblyName 为 `WorkFor.NativeBridge`，用于让真实 `NativeSession` 启动独立进程并验证 stdin / stdout 协议。它只有 BCL 依赖，没有注入、Windows 钩子、DLL 装载、命名管道或 Explorer 操作。

测试复制此程序到每例独立临时目录，并写入名为 `WorkFor.Taskbar.Live.dll` 的**普通 JSON 文本文件**，以 `fixture=true` 和 `mode` 选择行为。该文件只用于模式与指纹，不是 PE 文件，不会被加载。

输入为四字节 little-endian 长度前缀 JSON，输出为 JSONL。程序记录的启动标识和命令仅存于每例临时目录，用于断言真实进程行为；清理前同时核实可执行路径与进程创建时间，避免 PID 复用误清理其他进程。

模式覆盖正常配置确认、stdin EOF 后 Dormant、退出但曾报告 Attached、不支持版本、语法错误、超长输出、错误状态字段类型，以及忽略 EOF 的进程。测试验证实际 Host 监督实现，不在夹具中重写重试或隔离算法。

此项目设置 `IsPackable=false`、`IsPublishable=false`，不能包含在产品安装包或发布二进制中。
