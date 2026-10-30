# 定向验证与回归实验

该目录的程序只操作自己创建的 Win32 窗口 / XAML island，以及自己进程内的模拟 DLL。它不注入 Explorer，不重启 Explorer，不发送全局键鼠输入，也不操作其他应用界面。

## 运行

托管恢复预算测试：

```powershell
.\.tools\dotnet\dotnet.exe restore tests/WorkFor.Validation.Tests/WorkFor.Validation.Tests.csproj --locked-mode
.\.tools\dotnet\dotnet.exe test --project tests/WorkFor.Validation.Tests/WorkFor.Validation.Tests.csproj -c Debug --no-restore
```

原生独立测试工程：

```powershell
cmake -S experiments/validation -B out/validation-native -G "Visual Studio 18 2026" -A x64
cmake --build out/validation-native --config Debug --parallel 2
ctest --test-dir out/validation-native -C Debug --output-on-failure
```

上例使用发布基线的工具链。也可作为根 CMake 的 `add_subdirectory` 加入同一套 CTest。若使用不同生成器，应使用新的构建目录，不能直接重用不同工具链的 CMake 缓存。

本次本机原型实测使用已有 Visual Studio 2022 / MSVC 19.44、Windows SDK 26100；这是明确记录的原型工具链例外，不能据此声称已满足最新版发布工具链资格。

## 原生 12 组

| 用例 | 验证行为 |
|---|---|
| `dynamic-widths` | 180 / 360 / 260 DIP 在线调整，同时改变应用测量空间；NaN 和越界配置不生效 |
| `narrow-root` | 450 / 300 / 200 DIP 窄容器折叠收入、释放其列宽；空间恢复后重新显示 |
| `restore-local-values` | 逻辑移除恢复原零显式列及 attached property 的 UnsetValue，不写死默认值 |
| `existing-binding` | 已绑定布局在变更前拒绝；原 binding 保留且继续响应其数据源 |
| `unknown-layout` | 未识别结构不创建列、不添加控件 |
| `wrong-thread` | 非所有者线程不能修改 XAML 文本 |
| `unknown-fingerprint` | 自己进程装载原创模拟 Taskbar.dll，严格指纹准备拒绝，不触及私有 RVA |
| `long-text` | 180 DIP 下长金额与较长状态的视觉矩形不相交 |
| `font-enlargement` | 自己控件字体放大 1.5 / 2 倍后金额仍在 48 DIP 内；缩回后标题与状态恢复 |
| `theme-resources` | 真实框架资源从浅色到深色更换金额前景色 |
| `month-title` | 本月标题贯穿文本和自动化名称，断连更新不回退为今日；过长标题拒绝 |
| `activation` | 真实系统 ButtonAutomationPeer Invoke 触发激活，验证 DPI 与物理锚点，移除后不再触发 |

`fake-taskbar.cpp` 仅用于未知指纹测试。生成的 `Taskbar.dll` 必须停留在验证输出目录，不能被产品打包脚本递归收集到发行包。

字体放大通过修改自己的 TextBlock 字号模拟，不修改 Windows 文本缩放偏好。当前物理锚点实测为 144 DPI、RasterizationScale 1.5：260 DIP 对应 390 屏幕像素。100% / 200% 的不同实际显示器环境、真实键盘 Win+B / Tab 导航和高对比度偏好切换不在此次自动化实测范围内。

## 托管 32 项

其中恢复预算 21 项用自定义 TimeProvider 和每例独立临时目录验证：

- 三次相关失败触发隔离，Host 重启后保持。
- 未隔离计数按十分钟窗口过期；已隔离状态不自动重新尝试。
- 显式重试持久清除状态，新二进制使用独立预算。
- 损坏 JSON、缺少必要字段、未知 Schema、超限文件和错误字段类型保守隔离，不从 Host 构造函数逸出。
- 错误代码在持久化前有长度限制。

此组用真实文件与真实序列化入口验证，不把恢复预算逻辑复制到测试中；未新增第三方测试依赖。

另外 11 项直接实例化实际 `NativeSession`，启动 `tests/WorkFor.NativeFixture` 的独立 BCL 控制台进程。夹具只读写真实的带长度前缀 stdin / JSONL stdout，不复刻 Host 的监督算法，覆盖：

- 初始 / 更新宽度与 revision 的真实确认，过期 revision 不再发送到子进程，同 revision 的冲突配置拒绝。
- 正常 stdin EOF 后保留确认的 Dormant 状态；异常退出不会让 Host 留在 Attached。
- 不支持构建不自动重试，显式重试才再次启动。
- 三次相关进程退出触发持久隔离，新监督实例不会绕过已有隔离。
- 语法错误、超长输出及类型错误的合法 JSON 转为协议故障，错误字段不会泄漏进公开快照。
- 忽略 EOF 的协议故障进程在关闭截止后被终止；清理只匹配本例路径与创建时间。

夹具项目设置为不可打包 / 发布，不能进入产品安装包；它的同名 `.dll` 只是普通 JSON 模式文件，从未装载为 PE。

## 本轮修复依据

验证实际暴露并推动修复了：超长 / 缺字段恢复状态未隔离与异常类型漏捕获、窄容器仍保留旧应用宽度、attached property 未还原 UnsetValue、长金额与状态重叠、放大字号被裁切、本月金额误标今日，以及既有绑定被破坏导致逻辑移除失败。

最后已在对应当前源码上统一执行 12 组原生回归、32 项托管回归全部通过。结果摘要见 [results.json](results.json)。这些结果不代替真实 Explorer 的拥挤 / 溢出、多屏和版本升级资格测试。
