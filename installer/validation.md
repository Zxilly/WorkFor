# 本地发布与安装验收

日期：2026-10-30。当前为未签名 prototype；本机验证不等于全部 Windows / 多显示器资格通过。

## 构建

- .NET SDK 10.0.403 / 自包含 Runtime 10.0.14。
- Windows App SDK 2.6.4、Jint 4.17.0、Corvus.Text.Json.Validator 5.7.9。
- 项目本地 CMake 4.4.5、NSIS 3.13。
- 本机 MSVC 19.44 / Windows SDK 26100，未冒称已满足 VS 2026 / SDK 28000 基线。
- Debug / Release 的 148 项托管测试和 14 组原生测试通过；原生 UI 表单另有自有窗口实测。

## 本地包校验

| 文件 | 字节数 | SHA256 |
|---|---:|---|
| WorkFor-0.1.0-win-x64-prototype-3e10335c.zip | 174482406 | ca06f35b92e1850ca9b437f0516e492a17e6badaa7e6986d9d7e1d83b31e07fc |
| WorkFor-0.1.0-win-x64-prototype-3e10335c-Setup.exe | 95641307 | fafdfb3d949daf8fd184ff54f6f30754099e38f16b8ee810d420c5921548ce3e |

上述本地包在未提交工作区构建，release.json 会如实记录 sourceDirty；公开仓库的 CI 会从提交版本重新构建。包在 Git 忽略的 artifacts 目录，未把大二进制塞入源码 Git 历史。

验证通过：

- 启动器对 977 个发行文件执行完整 SHA256 校验。
- Host / ScriptHost / UI 的 includedFrameworks 均为 10.0.14。
- 无项目 DOTNET_ROOT 的实际计薪进程在固定时刻得到 32.00 元。
- NativeBridge 只对自有 ProbeTarget 做引导、帧协议与动态配置自测。
- 库存清理测试拒绝越界，保留额外文件和 launcher，仅移除已列出的文件 / 空目录。
- 分发依赖包含所需 BSD / MIT / Apache 许可与 NOTICE；没有旧 OSMF 校验器 / WiX。
- NSIS Setup 编译成功；Get-AuthenticodeSignature 返回 NotSigned，与元数据一致。

## 实际安装与卸载往返

另对相同版本、相同 NSIS 安装脚本的 `bfb6fd18` 验收包执行过真实往返；精确文件哈希和布尔结果见[记录](../experiments/validation/installer-roundtrip-result.json)。在安装根、工资数据目录、快捷方式、卸载注册及 Run 值均不存在时才执行：

1. 静默安装到固定当前用户目录，所有权标记 / 卸载注册 / 快捷方式正确。
2. 安装后运行 `WorkFor.exe --verify` 校验完整包，不启动应用或任务栏。
3. 写入启动器生成的正确引用命令，用于覆盖原来卸载时遗漏带引号参数的真实问题。
4. 执行卸载，确认文件、快捷方式、卸载注册和该启动命令全部移除；工资目录未被创建或修改。

没有通过安装测试向 Explorer 加载产品 DLL。最新版仅更新应用 payload，NSIS 往返配方及启动项清理逻辑相同；不把这次记录扩大为中断升级事务或所有环境通过。

复现脚本 `test-install-roundtrip.ps1` 默认只预检，必须显式 `-Execute`。发现已有安装、工资数据或注册项时直接拒绝，不以真实用户数据作为测试对象。

## 资格边界

- 任务栏 PIN 文件仍映射时，安装器会阻止覆盖 / 删除并要求注销后重试，不结束 Explorer。
- NSIS 路线没有宣称完整事务式升级回滚；断电 / 复制失败需要独立恢复资格测试。
- 未提供签名证书，因此发行包未签名。
- 远端 CI 状态以 GitHub Actions 实际 run 为准。
