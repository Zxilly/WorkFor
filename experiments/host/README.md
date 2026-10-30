# Host / ScriptHost 跨进程集成验证

日期：2026-10-30。测试只操作自己启动的 WorkFor 进程和临时配置目录，不安装任务栏钩子、不修改 Explorer。

## 运行

从仓库根目录执行：

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe run --project tests/WorkFor.Host.Tests/WorkFor.Host.Tests.csproj
```

测试工程会构建 Host、ScriptHost 和 Contracts。使用项目本地 .NET SDK 10.0.403，测试进程运行于 .NET 10.0.14；依赖由根 CPM 固定，NuGet lock 保存在测试工程目录。

Host 的命名管道按当前用户 SID / 会话定义，因此本套测试关闭并行运行。每次测试使用 `.local/host-tests/{GUID}` 配置目录，结束后停止自身进程树并删除该目录。客户端用 `GetNamedPipeServerProcessId` 确认连接的是自己启动的 Host，不会把同会话里已有的真实 WorkFor 实例当作测试对象。

运行前请关闭本会话的 WorkFor 开发 Host，否则测试会因单实例 / 管道身份不匹配明确失败。不要为通过测试而结束不属于自己的进程。

## 已验证的行为

| 行为 | 验证方式 |
|---|---|
| 每秒发布实时结果 | 活动规则使用上下文时间生成金额；读取同一代次中递增的 sequence、evaluatedAtMs 和变化的金额文本 |
| 候选预览独立 | `rule.preview` 返回后，`config.get` 的 revision 不变 |
| 原子规则激活 | `config.activate` 从 revision 1 提交为 2，显示快照随后切换至 revision 2 |
| 并发编辑冲突 | 再使用 expectedRevision 1 激活，收到 `CONFIG_CONFLICT`，当前规则保持不变 |
| 坏脚本保留旧规则 | 语法错误候选激活失败，源文、哈希与 revision 保持不变 |
| 重启恢复 | 停止并重启真正 Host 进程，已提交规则与 revision 保留，显示 generation 更新 |
| 协议错误隔离 | 超过上限的帧与重复 JSON 字段不会停止宿主 |
| 订阅断开回收 | 连续建立 / 断开显示订阅，次数超过并发上限后，健康客户端仍收到快照 |
| 死循环候选有界 | 无法返回的候选被解释器 / 外部预算拒绝，不替换活动规则，活动结果继续正常 |
| 无效配置明确拒绝 | 无效时区、空班次项和未知币种返回结构化错误；不改变 revision |
| 错误字段类型明确拒绝 | source 对象、parameters 字符串、display 数组均报错，不静默忽略后递增 revision |
| 原生管道饱和恢复 | 多个不发送握手的客户端填满 / 触发有界准入后退出，健康客户端仍能读取配置，listener 没有永久 fault |

没有用单元 mock 替代这些进程与 IPC 断言。测试也没有声称证明任务栏嵌入、Windows UI、安装或原生 DLL 的兼容性。

## 实验发现及修复

测试初次运行发现预览上下文的 evaluationId 不是规范要求的十进制字符串，导致脚本进程拒绝；Host 已改为序列号。

新增恶劣输入后确认无效时区和空班次项会导致控制连接直接结束；Host 已将配置规范化异常封装为结构化配置错误。

后续审查发现错误字段类型被静默忽略、管道达到原生实例上限可能使监听任务永久失败；新增回归测试并修复 Host 的候选类型校验与连接准入。最终 **10 项测试全部通过**，零编译警告 / 错误，执行时间 6.086 秒。

控制请求的执行预算也与握手预算分离，避免允许 10 秒冷启动的脚本进程被原有 5 秒握手预算提前打断。

一次并发 UI 实验尚在运行时，测试客户端的服务端 PID 检查在发送任何业务请求前拒绝连接，验证了误连保护；测试没有终止或修改该 UI 实验的 Host。协调让出管道后最终完整运行通过。
