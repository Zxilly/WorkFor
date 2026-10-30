(() => {
  const freeze = Object.freeze;
  const descriptors = Object.getOwnPropertyDescriptors;
  const keys = Object.keys;
  const proto = Object.getPrototypeOf;
  const plain = Object.prototype;
  const array = Array.isArray;
  const parse = JSON.parse;
  const stringify = JSON.stringify;
  const NativeError = Error;
  const NativeSet = Set;
  const ownKeys = Reflect.ownKeys;
  const own = Object.prototype.hasOwnProperty.call.bind(Object.prototype.hasOwnProperty);
  const canonical = /^(0|-[1-9][0-9]{0,23}|[1-9][0-9]{0,23})$/;
  const test = RegExp.prototype.test.call.bind(RegExp.prototype.test);
  function fail(code, message) { throw new NativeError(code + ": " + message); }
  function deepFreeze(value) {
    if (value && typeof value === "object") {
      for (const key of keys(value)) deepFreeze(value[key]);
      freeze(value);
    }
    return value;
  }
  function inspect(value, seen, depth) {
    if (depth > 8) fail("RESULT_INVALID", "Result nesting too deep");
    if (value === null || typeof value === "string" || typeof value === "boolean") return;
    if (typeof value === "number") {
      if (!Number.isFinite(value)) fail("RESULT_INVALID", "Non-finite number");
      return;
    }
    if (typeof value !== "object") fail("RESULT_INVALID", "Only JSON data is allowed");
    if (seen.has(value)) fail("RESULT_INVALID", "Cyclic result");
    if (!array(value) && proto(value) !== plain && proto(value) !== null) fail("RESULT_INVALID", "Plain objects required");
    seen.add(value);
    const entries = descriptors(value);
    for (const key of ownKeys(value)) {
      if (typeof key !== "string") fail("RESULT_INVALID", "Symbol properties are not JSON data");
      const property = entries[key];
      if (property.get || property.set) fail("RESULT_INVALID", "Accessors are not allowed");
      inspect(property.value, seen, depth + 1);
    }
    seen.delete(value);
  }
  function run(calculate, contextJson) {
    if (typeof calculate !== "function") fail("SCRIPT_ENTRY_INVALID", "Default export must be a function");
    const context = deepFreeze(parse(contextJson));
    const result = calculate(context);
    if (result && (typeof result === "object" || typeof result === "function")) {
      const then = descriptors(result).then;
      if (then || (typeof Promise !== "undefined" && result instanceof Promise)) fail("ASYNC_NOT_SUPPORTED", "Async rules are not supported");
    }
    inspect(result, new NativeSet(), 0);
    if (!result || array(result) || typeof result !== "object") fail("RESULT_INVALID", "Expected result object");
    const allowed = ["apiVersion", "currency", "amountMinor", "ratePerHourMinor", "note", "metrics"];
    for (const key of ownKeys(result)) if (!allowed.includes(key)) fail("RESULT_INVALID", "Unknown result field");
    if (result.apiVersion !== 1) fail("API_VERSION_UNSUPPORTED", "Expected result API version 1");
    if (result.currency !== context.currency.code) fail("CURRENCY_MISMATCH", "Result currency differs from context");
    if (typeof result.amountMinor !== "string" || !test(canonical, result.amountMinor)) fail("RESULT_INVALID", "Invalid amountMinor");
    if (own(result, "ratePerHourMinor") && (typeof result.ratePerHourMinor !== "string" || !test(canonical, result.ratePerHourMinor))) fail("RESULT_INVALID", "Invalid ratePerHourMinor");
    if (own(result, "note") && (typeof result.note !== "string" || result.note.length > 120)) fail("RESULT_INVALID", "Invalid note");
    if (own(result, "metrics")) {
      if (!array(result.metrics) || result.metrics.length > 4) fail("RESULT_INVALID", "Too many metrics");
      for (const metric of result.metrics) {
        if (!metric || typeof metric !== "object" || array(metric) || keys(metric).some(k => k !== "label" && k !== "value") || typeof metric.label !== "string" || metric.label.length > 20 || typeof metric.value !== "string" || metric.value.length > 64) fail("RESULT_INVALID", "Invalid metric");
      }
    }
    const json = stringify(result);
    if (json.length > 8192) fail("RESULT_INVALID", "Result exceeds size budget");
    return json;
  }
  const NativeDate = Date;
  function DeterministicDate(...args) {
    if (args.length === 0 || !new.target) fail("SCRIPT_EXCEPTION", "Use ctx.now.epochMs instead of current-time Date");
    return new NativeDate(...args);
  }
  DeterministicDate.prototype = NativeDate.prototype;
  DeterministicDate.parse = NativeDate.parse;
  DeterministicDate.UTC = NativeDate.UTC;
  DeterministicDate.now = () => fail("SCRIPT_EXCEPTION", "Use ctx.now.epochMs");
  Object.defineProperty(NativeDate.prototype, "constructor", { value: DeterministicDate, writable: false, configurable: false });
  Object.defineProperty(globalThis, "Date", { value: freeze(DeterministicDate), writable: false, configurable: false });
  Object.defineProperty(Math, "random", { value: () => fail("SCRIPT_EXCEPTION", "Randomness is disabled"), writable: false, configurable: false });
  Object.defineProperty(globalThis, "__workforRun", { value: run, writable: false, configurable: false });
  // Capture the harness before user code and prevent prototype sabotage of its helpers.
  for (const item of [Object, Object.prototype, Array, Array.prototype, Number, Number.prototype, String, String.prototype, Set, Set.prototype, RegExp, RegExp.prototype, JSON, Math, Reflect, BigInt, NativeDate.prototype]) freeze(item);
  for (const name of ["Object", "Array", "Number", "String", "Set", "RegExp", "JSON", "Math", "Promise", "Error", "BigInt", "Reflect"]) {
    Object.defineProperty(globalThis, name, { value: globalThis[name], writable: false, configurable: false });
  }
})();
