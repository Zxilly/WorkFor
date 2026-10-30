# 参数 Schema 校验器迁移与验证

日期：2026-10-30。

## 选型与确切版本

公开项目采用 MIT 许可证。原 `JsonSchema.Net 9.5.0` 的官方 NuGet 包包含 `OSMFEULA.txt`；本次改用 **Corvus.Text.Json.Validator 5.7.9** 的动态校验器，其官方包的许可证表达式是 `Apache-2.0`。这里使用的是可嵌入的库，不是 `Corvus.Json.Validator` 命令行工具，也不使用 Roslyn 或运行时 C# 代码生成。

版本直接从[官方 NuGet 版本索引](https://api.nuget.org/v3-flatcontainer/corvus.text.json.validator/index.json)核实，为本次查询时的最新稳定版。包内仓库提交是 `862901afeb36bfe4d7aace96fa0fb05c55bbe1cc`；API 与默认行为按[该版本源码](https://github.com/corvus-dotnet/Corvus.JsonSchema/blob/862901afeb36bfe4d7aace96fa0fb05c55bbe1cc/src/Corvus.Text.Json.Validator/Corvus/Text/Json/Validator/JsonSchema.cs)和包内 XML / README 校对，不依赖可能漂移的主分支示例。

运行依赖链新增：

| 包 | 版本 | 官方包许可证表达式 |
|---|---|---|
| Corvus.Text.Json.Validator | 5.7.9 | Apache-2.0 |
| Corvus.Text.Json | 5.7.9 | Apache-2.0 |
| NodaTime | 3.3.1 | Apache-2.0 |

Jint 4.17.0 和其 Acornima 1.7.0 解析器继续使用，分别为 BSD-2-Clause 和 BSD-3-Clause。项目的 MIT 许可证不替代这些依赖的许可证，分发文件应保留各包的相应版权与许可资料。

## 校验行为

`RulePackageValidator` 仍运行在独立候选 ScriptHost 进程内，保留 Host 的 2 秒外部截止及进程资源限制。正常运行不读取用户 Schema 引用的文件或 URL。

```csharp
new JsonSchema.Options(
    allowFileSystemAndHttpResolution: false,
    defaultDialect: JsonSchemaDialect.Draft202012,
    alwaysAssertFormat: false,
    codeGeneration: JsonSchemaCodeGeneration.Disabled,
    regexMatchTimeout: TimeSpan.FromMilliseconds(20));
```

这些值必须显式指定：Corvus 的默认选项会允许文件 / HTTP 引用，并始终断言 `format`，不适合直接用于本项目。`alwaysAssertFormat=false` 保留 Draft 2020-12 的常规注解行为；例如只写 `format: "email"` 不等于启用该方言的格式断言词汇。

校验顺序：

1. 检查清单、源文、JSON 深度与大小等产品限制。
2. 只在真正的 Schema 位置遍历引用策略；禁止外部 / 相对引用、URI 重映射及循环，仅支持受限的本地 `$defs` 引用。
3. 使用库内嵌的 Draft 2020-12 标准元 Schema 检查参数 Schema 本身，拒绝 `required` / `minimum` 等关键字的非法数据类型。
4. 用 Schema 内容 SHA-256 生成内部 canonical URI，避免不同 Schema 共享一个缓存键。
5. 由 Corvus 的完整运行时引擎执行 JSON Schema 语义，不用手写的小子集替代组合、数组或条件关键字。

产品 API v1 的引用限制仍然有效，底层支持 Draft 2020-12 不表示开放动态引用、任意文件或网络加载。Schema 中 `const`、`default`、`enum` 的普通 JSON 数据不会被当成 Schema 子图；名为 `$id` 的业务属性也不会误报为 URI 重映射。

正则匹配使用 20ms 单次上限；超时转为结构化的 `MANIFEST_INVALID`。解析 / 编译 / 组合校验整体仍受候选进程的外部截止约束，正则限额不被当作完整进程安全沙箱。

## 实际验证

执行命令：

```powershell
./.tools/dotnet/dotnet.exe test --project tests/WorkFor.ScriptHost.Tests/WorkFor.ScriptHost.Tests.csproj
```

迁移后 **56 项测试全部通过**，其中原有 40 项保持通过，新增 16 项覆盖：

- Draft 2020-12 的 `unevaluatedProperties` 跨组合跟踪、`prefixItems` / `items`、`contains` 计数、条件与依赖 Schema。
- 标准元 Schema 对错误关键字类型的拒绝。
- `true` / `false` Schema、本地 JSON Pointer 转义和 `$defs` 引用。
- 外部 URL、文件引用、相对路径、嵌套动态引用及循环的拒绝。
- 普通数据中的 `$id` / `$ref` 不触发加载或错误拒绝。
- `format` 常规注解语义。
- 复杂正则的有界失败，以及失败后的下一次正常校验恢复。

本次仅构建 / 测试 ScriptHost 工程，未修改真实 Explorer、用户工资或 GitHub。其他消费项目的 NuGet 锁文件需要在整合时统一恢复。
