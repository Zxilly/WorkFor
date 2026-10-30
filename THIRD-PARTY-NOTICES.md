# 第三方许可资料

WorkFor 自有源码采用 [MIT](LICENSE)。第三方库、运行时和构建工具保留各自许可；项目 MIT 不替代它们。

| 依赖 | 当前版本 | 上游许可 / 来源 |
|---|---|---|
| Jint | 4.17.0 | [BSD-2-Clause](https://github.com/sebastienros/jint) |
| Acornima | 1.7.0 | [BSD-3-Clause](https://github.com/adams85/acornima) |
| Corvus.Text.Json.Validator / Corvus.Text.Json | 5.7.9 | [Apache-2.0](https://github.com/corvus-dotnet/Corvus.JsonSchema) |
| NodaTime | 3.3.1 | [Apache-2.0 与 NOTICE](https://github.com/nodatime/nodatime) |
| nlohmann/json | 3.12.0 | [MIT](https://github.com/nlohmann/json) |
| .NET | 10.0.14 | [运行时源码与分发许可资料](https://github.com/dotnet/runtime) |
| Windows App SDK | 2.6.4 | [源码与包内分发许可资料](https://github.com/microsoft/WindowsAppSDK) |
| NSIS（安装器构建） | 3.13 | [zlib/libpng 等原始许可](https://nsis.sourceforge.io/Docs/AppendixI.html)，本项目使用 zlib 压缩 |

`scripts/package.ps1` 会收集恢复依赖的 NuGet 元数据、包内许可 / NOTICE，以及固定上游提交的 BSD、MIT、Apache 许可文本；来源与 SHA256 固定。自包含运行时原有的许可资料也保留在各输出目录。

发行包中的 `third-party`、`THIRD-PARTY-NOTICES.md` 和各运行时原始资料应一起分发，不能仅复制若干 EXE / DLL 后丢弃这些文件。

早期设计中的 JsonSchema.Net / WiX 维护费二进制没有进入当前发行依赖，具体迁移说明见 [Schema 迁移](docs/schema-migration.md) 和 [安装说明](installer/README.md)。
