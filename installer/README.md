# 发布与安装

构建基线：2026-10-30。交付 portable zip 与 **NSIS 3.13** 每用户 Setup.exe，两者都有原生 WorkFor.exe 入口，用户无需安装 PowerShell、.NET SDK 或 Windhawk。

## 构建

```powershell
./scripts/build.ps1 -Configuration Release -Test
./scripts/package.ps1 -Configuration Release -Setup
```

开发机器未满足文档指定的最新稳定 MSVC / CMake 时，必须显式使用 -PrototypeToolchain。文件名与 release.json 会标明 prototype，不能把旧工具链说成最新版本。

NSIS 使用[官方稳定版 3.13](https://nsis.sourceforge.io/Docs/AppendixF.html)，发布于 2026-09-27；3.14 文档条目尚没有发布日期。[官方 SourceForge 下载页](https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download)提供的 SHA-256 固定为 ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8。bootstrap 先尝试 SourceForge；网络阻断时仅接受与同一官方哈希完全一致的 Tauri 官方镜像包。工具保存在 .tools，不全局安装。

安装器使用 zlib 压缩并保留 NSIS COPYING。没有接受或绕过 WiX 7 的 OSMF EULA，WiX 不再是构建或运行依赖。

## 发布目录与运行时

Host、ScriptHost、WinUI 3 分别自包含发布到 app/{version}/{host,script,ui}，固定 .NET Runtime 10.0.14；原生 EXE / DLL 位于 native 子目录。不合并各工程同名 DLL，避免 .NET 和 WinUI 冲突。

UI 使用固定 SDK 10.0.403 的最新运行时补丁选择，并检查发布 runtimeconfig.json 确实包含自包含 Microsoft.NETCore.App 10.0.14。不将全局 RuntimeFrameworkVersion 错误套用到 Windows SDK 引用包；输出版本不符则打包失败。

根 release.json 记录各文件的 SHA-256、路径、工具链与 unsigned 状态。普通启动先核实必需 EXE / 原生 DLL，随后按绝对路径启动各模块。

| 命令 | 行为 |
|---|---|
| WorkFor.exe | 启动后台和任务栏，打开收入详情 |
| WorkFor.exe --background | 只启动后台和任务栏 |
| WorkFor.exe --settings | 后台不存在时先启动，再打开设置 |
| WorkFor.exe --quit | 请求本安装根的 Host 退出，并关闭自己的 UI |
| WorkFor.exe --verify | 校验完整包，不启动应用 |
| WorkFor.exe --prepare-uninstall | 安装器内部前置检查：固定根、所有权标记、正常退出和 DLL PIN |
| WorkFor.exe --uninstall-files | 再次前置检查后，仅删除库存文件及空目录 |

没有提供证书，产物未签名；没有生成虚假签名。

## 安装、升级与卸载

固定安装到 %LOCALAPPDATA%\Programs\WorkFor，不开放任意目录选择，拒绝目录重解析点。已有内容但没有正确产品标记时拒绝覆盖。开始菜单提供应用与设置入口。

用户工资 / 规则数据位于独立 %LOCALAPPDATA%\WorkFor，不在安装清理范围。原生 helper 校验实际已知文件夹、非根路径和产品标记后，才请求本目录 Host / UI 退出，并检查当前用户 Explorer 是否仍映射本根的 WorkFor 任务栏 DLL。

PIN 未结束时明确拒绝升级或卸载，提示注销并重新登录后重试。不会终止 Explorer，不把逻辑移除收入区域当作 DLL 已物理卸载。

卸载按 release.json 逐文件处理，所有目标先规范化并验证仍在本根内，再开始删除；仅尝试删除空目录。没有 RMDir /r 或整个目录树递归删除。不在库存内的额外文件保留。NSIS 在 helper 正常退出后只处理自己的 launcher、marker、清单、uninstaller 及精确注册表 / 快捷方式项。

升级先安全停止旧版本并按旧版本库存移除旧文件，再提取新 payload。NSIS 不提供这里尚未实现的完整事务式回滚；异常断电或复制失败可能需要重新运行 Setup 修复。用户工资目录不被删除。

`package.ps1` 只编译 Setup，不自动执行。另有 `test-install-roundtrip.ps1` 默认预检、显式 `-Execute` 后才实际安装；本机空目录往返已通过，见[验收记录](validation.md)。中断升级和跨系统矩阵仍需独立验证。

## 验证

包构建默认执行：

1. WorkFor.exe --verify，校验全部发布文件。
2. portable Host / ScriptHost 在移除项目 DOTNET_ROOT 的环境计算固定 2026-10-30 11:00 时薪结果，断言 32.00 元。
3. NativeBridge --self-test，只对自己启动的 ProbeTarget 验证引导生命周期。

installer/test-launcher.ps1 用受控、从不执行的伪文件验证完整库存、篡改、路径越界和必需 DLL 遗漏；不声称这些伪文件是可运行发布包。

最终 zip / Setup 在忽略的 artifacts 中。恢复锁、工具链状态、第三方许可和 NuGet 元数据随包保存。项目 MIT 不替代 .NET、Windows App SDK、Jint、Acornima、Corvus、NodaTime 等依赖的许可。

本次实际构建结果、产物哈希及验证边界见[本地验证记录](validation.md)。
