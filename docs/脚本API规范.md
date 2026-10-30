# JavaScript 脚本 API 规范 v1

设计日期：2026-10-30。状态：API v1 的 SDK、运行时与契约测试已实现；实际支持及发布限制见[实现进展](实现进展.md)。

该规范定义用户规则如何计算收入。Windows 任务栏、定时器、文件与 UI 均由原生宿主管理；脚本负责同步计算并返回数据。配套完整类型见 [workfor-api.d.ts](examples/workfor-api.d.ts)。

## 1. 最小规则

```js
import { money } from "@workfor/sdk";

export default function calculate(ctx) {
  const rate = money.fromDecimal(
    ctx.parameters.hourlyRate,
    ctx.currency.fractionDigits
  );

  return {
    apiVersion: 1,
    currency: ctx.currency.code,
    amountMinor: money.ratio(
      rate,
      ctx.periods[ctx.activePeriod].elapsedScheduledMs,
      3_600_000,
      "floor"
    ),
    ratePerHourMinor: rate
  };
}
```

`calculate` 每秒由宿主调用一次。脚本不调用 `setInterval`，也不把每秒赚到的金额存入变量。相同上下文应得到相同结果；程序退出、睡眠恢复后仍可直接根据绝对时刻重算。

## 2. 规则包格式

一个规则包只有清单和入口脚本；首版不支持任意多文件依赖或 npm 包。

```text
my-hourly-rule/
  rule.json
  index.mjs
```

```json
{
  "manifestVersion": 1,
  "apiVersion": 1,
  "id": "custom.my-hourly-rule",
  "name": "我的时薪规则",
  "version": "1.0.0",
  "description": "按计薪日历计算当前周期的收入",
  "entry": "index.mjs",
  "parametersSchema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["hourlyRate"],
    "properties": {
      "hourlyRate": {
        "title": "时薪",
        "type": "string",
        "pattern": "^(0|[1-9][0-9]{0,11})(\\.[0-9]{1,2})?$",
        "default": "16.00"
      }
    }
  }
}
```

此参数模式是人民币示例；其他币种精度由对应规则 / 参数验证调整。`default` 仅用于首次创建配置时填入默认值，JSON Schema 校验器不会自动替用户补值。

| 字段 | 约束 |
|---|---|
| `manifestVersion` | 必须为 `1` |
| `apiVersion` | 必须为 `1`，与返回值一致 |
| `id` | 1—80 个 ASCII 字符，`[a-z0-9][a-z0-9._-]*`；内置规则保留 `builtin.` 前缀 |
| `name` | 1—40 个 Unicode 字符，界面按纯文本展示 |
| `version` | SemVer 规则版本；代码内容另以 SHA-256 唯一标识 |
| `description` | 可选，最多 200 个字符 |
| `entry` | 首版固定 `index.mjs`；不允许绝对路径、`..` 或 URL |
| `parametersSchema` | JSON Schema Draft 2020-12，最多 32KiB、深度最多 16 |

清单加源文总量不超过 96KiB，源文不超过 64KiB。导入时复制到本地不可变规则目录，规则不是一个可以请求任意文件的包管理器。

### 参数配置 UI

宿主通过 ScriptHost 的独立候选验证模式调用 JsonSchema.Net 校验参数，自定义 Schema 的求值不在常驻 Host 线程执行；验证进程使用单独 Job 和 2 秒外部截止，失败不影响活动计算。只对简单的 `string`、`number`、`integer`、`boolean`、`enum`、`title`、`description` 生成原生输入控件；复杂对象允许在原生 JSON 文本框中编辑，并仍经过完整校验。

禁止远程 `$ref`；只允许文档内 `#/$defs/...` 引用，限制解析深度与循环。清单的 `$schema` 字符串用于标识方言，不发起 HTTP 请求。`pattern` 校验同样受正则与外部执行预算约束。缺少必要字段、额外字段和超过范围均不激活规则。

## 3. 入口与运行环境

```ts
type Calculate = (ctx: WorkForContext) => RuleResult;
```

- 必须导出一个默认同步函数。
- 返回 `Promise`、`async function` 或 thenable 均产生 `ASYNC_NOT_SUPPORTED`。
- 只允许 `import { money, time, apiVersion } from "@workfor/sdk"`。
- 不支持用户相对模块导入、`require`、动态 `import()` 和 top-level await。
- SDK 是项目自有的纯 JS 内存模块，运行时不访问 npm，不导出 CLR 类型。
- 每次求值新建 Jint Engine；顶层变量可以作为本次计算临时值，但不会跨 tick 保留。
- `ctx`、SDK 入口与其暴露对象递归冻结；脚本不能通过修改输入影响下一次计算。
- 提供普通 ECMAScript 数据、数组、`BigInt` 与同步数学能力；目标兼容范围以锁定 Jint 版本的契约测试为准，不承诺完整浏览器或 Node 环境。
- `eval`、`Function` 字符串编译关闭；无 `window`、`document`、`process`、`fetch`、文件访问、定时器、宿主反射和网络加载。
- 禁用 `Math.random` 与直接读取实时钟的 `Date.now`；使用 `ctx.now.epochMs`。规范也禁止 `new Date()` 隐式取当前时刻；需要 Date 时只能传入上下文中的时间戳。

金额格式化不依赖 JS `Intl` 的可用性，交给 .NET 宿主根据语言和币种执行。脚本不是 UI 模板，不能返回 HTML、XAML、CSS 或可执行命令。

## 4. 输入上下文

```json
{
  "apiVersion": 1,
  "evaluationId": "8451",
  "configRevision": 7,
  "calendarRevision": 3,
  "now": {
    "epochMs": 1793329200000,
    "timeZone": "Asia/Shanghai",
    "localDate": "2026-10-30",
    "localTime": "11:00:00",
    "isoWeekday": 5,
    "utcOffsetMinutes": 480
  },
  "activePeriod": "day",
  "currency": { "code": "CNY", "fractionDigits": 2 },
  "parameters": { "hourlyRate": "16.00" },
  "periods": {
    "day": {
      "kind": "day",
      "key": "2026-10-30",
      "startMs": 1793289600000,
      "endMs": 1793376000000,
      "scheduledMs": 28800000,
      "elapsedScheduledMs": 7200000,
      "elapsedWallMs": 39600000
    },
    "month": {
      "kind": "month",
      "key": "2026-10",
      "startMs": 1790784000000,
      "endMs": 1793462400000,
      "scheduledMs": 633600000,
      "elapsedScheduledMs": 612000000,
      "elapsedWallMs": 2545200000,
      "scheduledBeforeDayMs": 604800000
    }
  },
  "schedule": {
    "segments": [
      { "startMs": 1793322000000, "endMs": 1793332800000 },
      { "startMs": 1793336400000, "endMs": 1793354400000 }
    ]
  }
}
```

上例月份按周一至周五、每天 8 小时、没有日期覆盖计算。`schedule.segments` 为节省篇幅仅展示当天两个区间；实际输入必须提供当前整月的完整规范化区间，最多 512 段，不能真的只传这两段再声称与月度统计一致。

### 4.1 顶层字段

| 字段 | 说明 |
|---|---|
| `apiVersion` | 运行时契约主版本，固定为 `1` |
| `evaluationId` | 宿主分配的十进制字符串，只作日志关联，不用于计算 |
| `configRevision` | 当前配置版本，整数 |
| `calendarRevision` | 日历版本，整数 |
| `now` | 同一次评估的唯一当前时刻及宿主转换后的本地日期 |
| `activePeriod` | `day` 或 `month`，由用户展示配置决定 |
| `currency` | 显示币种及最小单位精度 |
| `parameters` | 已通过清单 Schema 校验的 JSON 参数；对象无 CLR 绑定 |
| `periods` | 当前日 / 月的规范化统计，均基于配置时区 |
| `schedule.segments` | 当前月完整计薪区间，UTC 毫秒、升序、无重叠 |

输入中的所有 Number 必须是安全整数或经过 Schema 限定的有限数值。时间戳和毫秒时长使用安全整数；金额不使用 Number。

### 4.2 日期与日历

`isoWeekday` 为周一 1 至周日 7。`utcOffsetMinutes` 为本地时区相对 UTC 的偏移，例如上海为 `480`。

`periods.day` 按目标时区的本地午夜定义；`periods.month` 从本月一日到下月一日。周期是 `[startMs, endMs)`，不是“滚动过去 24 小时 / 30 天”。

`schedule.segments` 已应用：星期计划、日期覆盖、跨日切分、休息区间扣除、重叠合并及月份裁剪。用户脚本无需猜测节假日或再次扣午休。

| 周期字段 | 精确定义 |
|---|---|
| `scheduledMs` | 所有计薪区间与该周期相交的总长度 |
| `elapsedScheduledMs` | 计薪区间与 `[周期开始, min(now, 周期结束))` 的相交长度，最小为 0 |
| `elapsedWallMs` | `clamp(now - startMs, 0, endMs - startMs)`；包括非计薪时段 |
| 月份 `scheduledBeforeDayMs` | 本月计薪区间在当前本地日开始之前的长度，供月薪日分摊计算使用 |

始终满足 `0 <= elapsedScheduledMs <= scheduledMs`，区间外不增长。日 / 月边界与 DST 由宿主解决，不能把每天强行设为 86,400,000ms。

### 4.3 币种与精度

```ts
interface CurrencyInfo {
  code: string;           // 大写三字母，例如 CNY / JPY / KWD
  fractionDigits: number; // 例如 2 / 0 / 3
}
```

宿主使用版本化的币种元数据表验证码与精度，首版支持精度 0—4。此表由应用发布维护，不从 Windows 当前区域设置猜测任意币种的小数位，也不联网换汇。

`CNY + "12860"` 表示人民币 128.60 元；`JPY + "12860"` 表示 12,860 日元；`KWD + "12860"` 表示 12.860 科威特第纳尔。

## 5. 返回结果

```ts
interface RuleResult {
  apiVersion: 1;
  currency: string;
  amountMinor: string;
  ratePerHourMinor?: string;
  note?: string;
  metrics?: Array<{ label: string; value: string }>;
}
```

```json
{
  "apiVersion": 1,
  "currency": "CNY",
  "amountMinor": "12860",
  "ratePerHourMinor": "1600",
  "note": "按计划计薪区间计算",
  "metrics": [
    { "label": "计算方式", "value": "时薪" }
  ]
}
```

### 输出约束

- `amountMinor` / `ratePerHourMinor` 均为最小单位的**十进制整数字符串**，符合 `^(0|-[1-9][0-9]*|[1-9][0-9]*)$`，绝对值最多 24 位。不接受 `-0`、前导零、`+`、空白、科学计数法或小数点。
- 可以返回负金额，表示用户自定义扣减结果；宿主不擅自截断为零。
- `currency` 必须与上下文一致；返回另一币种产生 `CURRENCY_MISMATCH`，不能只替换符号。
- `ratePerHourMinor` 是**每小时**的最小单位金额，与内部刷新频率无关；不返回则 UI 隐藏时薪行。
- `note` 最多 120 个字符；`metrics` 最多 4 项，label 最多 20 字符、value 最多 64 字符。默认 UI 除计薪时长外，只展示必要的附加信息。
- 返回值限普通 JSON 数据，禁止返回 `BigInt`、函数、Date、循环引用或访问器属性。所有序列化操作仍受预算与硬截止约束；不能声称可靠地静态识别所有 Proxy。
- 结果 JSON 正文最多 8KiB、深度最多 8；新增未知顶层字段在 v1 严格校验时拒绝，避免脚本误以为任意 UI 参数会生效。
- 标题、连接状态、成功时间和刷新状态由 Host 决定，脚本不能把错误伪装成“实时更新”。

宿主金额格式化使用整数 / BigInteger 与币种精度生成显示值，不将金额先转为 JS Number 或 .NET `double`。任务栏宽度不足时只压缩展示，不截断底层数值；完整值可在详情和辅助功能名称中查看。

## 6. SDK 函数

### 6.1 `apiVersion`

```js
import { apiVersion } from "@workfor/sdk";
// apiVersion === 1
```

### 6.2 `money.fromDecimal(value, fractionDigits)`

将主货币单位十进制字符串转换成最小单位字符串，严格转换，不自动舍入。

```js
money.fromDecimal("16.00", 2);   // "1600"
money.fromDecimal("16", 2);      // "1600"
money.fromDecimal("-1.25", 2);   // "-125"
money.fromDecimal("12.860", 3);  // "12860"
money.fromDecimal("12860", 0);   // "12860"
money.fromDecimal("1.005", 2);   // 抛出 SDK_PRECISION
money.fromDecimal("1e3", 2);     // 抛出 SDK_ARGUMENT
```

输入长度最多 32 字符，精度只能为整数 0—4，禁止空白和本地千位分隔符；可接受 `-0.00` 但统一输出 `"0"`。转换后超过 24 位则报 `SDK_RANGE`。填充缺省小数位以字符串操作完成，不经过浮点数。

### 6.3 `money.ratio(minor, numerator, denominator, rounding)`

```ts
ratio(
  minor: string,
  numerator: number,
  denominator: number,
  rounding: "floor" | "towardZero" | "halfAwayFromZero"
): string
```

计算 `BigInt(minor) * BigInt(numerator) / BigInt(denominator)`，只在最终商上按指定模式舍入。numerator 必须为非负安全整数；denominator 必须为正安全整数。禁止 `NaN`、`Infinity`、小数、零分母和隐式 Number 转换。中间量可超过 24 位，最终结果仍须在范围内。

| 模式 | 精确定义 | 示例 |
|---|---|---|
| `floor` | 向负无穷取整 | `3/2 -> 1`；`-3/2 -> -2` |
| `towardZero` | 向零取整 | `3/2 -> 1`；`-3/2 -> -1` |
| `halfAwayFromZero` | 四舍五入，中点远离零 | `3/2 -> 2`；`-3/2 -> -2` |

JS 的 BigInt `/` 原生为向零截断；SDK 必须专门处理负数 floor 与中点舍入，不能直接把除法结果冒充所有舍入策略。

```js
money.ratio("1600", 7_200_000, 3_600_000, "floor"); // "3200"
money.ratio("1600", 28_935_000, 3_600_000, "floor"); // "12860"
money.ratio("-3", 1, 2, "floor");                   // "-2"
```

### 6.4 `money.add` / `money.subtract`

```js
money.add("12860", "500");      // "13360"
money.subtract("12860", "500"); // "12360"
```

操作数均为规范化整数字符串，同单位使用；SDK 不自动换币。返回值使用相同 24 位限制。

### 6.5 `time.elapsedMs(segments, untilMs)`

计算规范化区间在 `untilMs` 之前的相交总长度。

```js
time.elapsedMs(
  [{ startMs: 1000, endMs: 5000 }, { startMs: 8000, endMs: 12000 }],
  10000
); // 6000
```

时间戳须为安全整数；区间须满足 `endMs > startMs`、升序、不重叠，总长度在安全整数范围内。不符合则抛出 `SDK_ARGUMENT`。该函数不自动合并用户传入的错误区间，以免掩盖重复计薪。

### 6.6 `time.clip(segments, startMs, endMs)`

返回区间与 `[startMs, endMs)` 的交集，保留顺序，移除空段；输入不变。start 与 end 均为安全整数，允许相等，禁止 start 大于 end。

```js
const todaySegments = time.clip(
  ctx.schedule.segments,
  ctx.periods.day.startMs,
  ctx.periods.day.endMs
);
```

返回新数组，最多 512 段。`elapsedMs` 和 `clip` 的组合可支持用户自定义某个时段统计，但默认规则直接使用宿主提供的周期统计即可。

## 7. 完整示例

### 7.1 时薪：今天或本月累计

入口见本文件第一节。清单的 `hourlyRate` 为主单位字符串。展示范围切换 `day` / `month` 后，计算仍只使用 `ctx.periods[ctx.activePeriod]`。

时薪 16 元、已计薪两小时，返回 `"3200"`，宿主显示 `¥32.00`。午休时已计薪时长不变；下一工作区间开始后自动增长。即使睡眠错过一万次 tick，重算仍得到相同时刻应得结果。

### 7.2 月薪：按本月计划计薪时长分摊

参数：`{ "monthlySalary": "16000.00" }`。不能把“月薪 / 22 / 8”写成不可更改的宿主默认常量；本月工作日和日期覆盖由日历提供。

```js
import { money } from "@workfor/sdk";

export default function calculate(ctx) {
  const salary = money.fromDecimal(
    ctx.parameters.monthlySalary,
    ctx.currency.fractionDigits
  );
  const month = ctx.periods.month;

  if (month.scheduledMs === 0) {
    throw new Error("CALENDAR_EMPTY: 本月没有计薪区间");
  }

  const accrued = money.ratio(
    salary, month.elapsedScheduledMs, month.scheduledMs, "floor"
  );
  const beforeToday = money.ratio(
    salary, month.scheduledBeforeDayMs, month.scheduledMs, "floor"
  );

  return {
    apiVersion: 1,
    currency: ctx.currency.code,
    amountMinor: ctx.activePeriod === "month"
      ? accrued
      : money.subtract(accrued, beforeToday),
    ratePerHourMinor: money.ratio(
      salary, 3_600_000, month.scheduledMs, "halfAwayFromZero"
    ),
    note: "按本月计划计薪时长分摊；时薪为折算值"
  };
}
```

日收入采用“本月截至当前累计值 - 截至今天开始累计值”。不把每天单独四舍五入后相加，从而使每天最终差额的总和与完整月薪一致。月末全部计薪区间结束后，月累计准确等于月薪最小单位值。

月中修改月薪或日历会按新配置重新计算整个当前周期，金额可能变化。这是 v1 的显式语义；若需要按生效日分段记账，必须新增版本化账本，不能悄悄混用旧值和新值。

### 7.3 自定义：时薪收入加固定补贴

参数：`{ "hourlyRate": "16.00", "allowance": "20.00" }`。

```js
import { money } from "@workfor/sdk";

export default function calculate(ctx) {
  const rate = money.fromDecimal(
    ctx.parameters.hourlyRate, ctx.currency.fractionDigits
  );
  const allowance = money.fromDecimal(
    ctx.parameters.allowance, ctx.currency.fractionDigits
  );
  const elapsed = ctx.periods[ctx.activePeriod].elapsedScheduledMs;
  const base = money.ratio(rate, elapsed, 3_600_000, "floor");

  return {
    apiVersion: 1,
    currency: ctx.currency.code,
    amountMinor: money.add(base, elapsed > 0 ? allowance : "0"),
    ratePerHourMinor: rate,
    note: "当前展示周期开始计薪后发放一次固定补贴"
  };
}
```

该示例明确是“每个当前展示周期一次补贴”；在 month 模式下不自动变成每日补贴。用户若要每日发放，需要基于日历逐日计算，或另用专门规则，避免展示范围改变导致含义不清。

## 8. 错误契约

```json
{
  "type": "evaluation.error",
  "requestId": "8451",
  "payload": {
    "code": "SCRIPT_SYNTAX",
    "message": "入口脚本第 12 行语法错误",
    "file": "index.mjs",
    "line": 12,
    "column": 8,
    "retryable": false
  }
}
```

`line` / `column` 对用户均为从 1 开始；内部源位置需转换。SDK 内部错误向用户显示稳定错误码，调试日志另保留内部堆栈；不把本地私人路径或完整脚本抄进默认日志。

| 错误码 | 条件 | 活动规则行为 |
|---|---|---|
| `MANIFEST_INVALID` / `PARAMETER_INVALID` | 清单或参数校验失败 | 候选不激活 |
| `API_VERSION_UNSUPPORTED` | 主版本不支持 | 不执行 |
| `MODULE_NOT_ALLOWED` | 尝试导入非 SDK 模块 | 不激活 / 保留最后结果 |
| `SCRIPT_SYNTAX` | 解析失败 | 候选不激活 |
| `SCRIPT_ENTRY_INVALID` | 默认导出不是函数 | 候选不激活 |
| `SCRIPT_EXCEPTION` | 用户抛出异常 | 保留最后成功结果并标错 |
| `SCRIPT_TIMEOUT` | 协作预算或外部截止超过 | 标错，必要时终止 ScriptHost |
| `SCRIPT_LIMIT` | 语句 / 内存 / 递归等限制 | 标错，按故障退避 |
| `ASYNC_NOT_SUPPORTED` | Promise / thenable / 异步代码 | 拒绝结果 |
| `RESULT_INVALID` | 类型、长度、数值格式、字段不合法 | 拒绝结果 |
| `CURRENCY_MISMATCH` | 返回与配置不同的币种 | 拒绝结果 |
| `SDK_ARGUMENT` / `SDK_PRECISION` / `SDK_RANGE` | SDK 参数、精度、范围错误 | 显示对应调用错误 |
| `SCRIPT_HOST_EXITED` | 子进程退出或被 Job 终止 | 重建进程，旧结果标为失效 |

用户 `throw new Error("CALENDAR_EMPTY: ...")` 的基础错误码仍为 `SCRIPT_EXCEPTION`，正文保留用户消息；它不会自动增加一个未登记的协议错误码。

## 9. 宿主装载参考

以下为针对 Jint 4.17.0 的实现方向，非已经编译通过的成品；模块加载策略、上下文转换和有界编码必须作为单独实现完成。

```csharp
var engine = new Engine(options =>
{
    options.Strict();
    options.DisableStringCompilation();
    options.MaxStatements(100_000);
    options.LimitMemory(16 * 1024 * 1024);
    options.TimeoutInterval(TimeSpan.FromMilliseconds(100));
    options.CancellationToken(cancellationToken);
    options.LimitRecursion(64);
    options.RegexTimeoutInterval(TimeSpan.FromMilliseconds(20));
});

engine.Modules.Add("@workfor/sdk", sdkSource);
engine.Modules.Add("workfor:user", userSource);
var userModule = engine.Modules.Import("workfor:user");
var entry = userModule.Get("default");

// 后续：严格验证入口、把 JSON 解析为 JS 原生对象、冻结输入，
// 在预算内调用并有界编码结果。不要 SetValue 一个富 CLR 上下文对象。
```

还必须明确拒绝未登记模块的 loader，并确保禁止动态导入；模块默认行为不能代替 allowlist 测试。对于解析阶段不能被解释器执行预算覆盖的耗时，外部进程截止仍然有效。

JSON 编码前验证返回值结构，不直接对任意 CLR 投影调用 `ToObject()`。Promise / 循环对象 / 超长字符串 / getter 等路径都必须进入同一个执行截止。

## 10. 契约版本与测试

`apiVersion` 为主版本；应用功能版本、规则 SemVer、SDK 实现版本彼此分开。v1 不改变既有字段语义，不修改金额舍入结果。增加影响旧脚本的必填字段、状态持久化或异步能力时，发布 v2，并允许用户显式迁移。

规则更新前验证语法、参数 Schema、入口和代表时刻；验证成功再替换活动哈希。版本号相同但内容变化仍生成新哈希，不覆盖旧目录。

必需的契约测试包括：

1. 同一上下文多次运行结果一致，顶层计数器不跨调用增长。
2. 所有禁止模块 / CLR / 文件 / 定时器 / 网络路径被拒绝。
3. 返回 Promise、循环对象、getter、非有限数、大整数字符串与超长文本得到有界错误。
4. 所有币种精度、负数舍入、跨零运算与 24 位边界。
5. 日 / 月金额、午休、节假日、睡眠前后、DST、月末完整薪资与分日差额总和。
6. 旧版本在途结果不能覆盖新规则，预览超时不能影响活动计算。
7. 解释器超时无法收敛时，Host 在外部截止后终止子进程，Explorer 保持可响应。

文档附带的规则示例只作为接口参考，不能替代上述 Jint 集成测试和原生任务栏验证。
