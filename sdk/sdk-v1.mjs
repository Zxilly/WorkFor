const MAX = 999999999999999999999999n;
const canonical = /^(0|-[1-9][0-9]{0,23}|[1-9][0-9]{0,23})$/;
function fail(code, text) { throw new Error(code + ": " + text); }
function read(value) {
  if (typeof value !== "string" || !canonical.test(value)) fail("SDK_ARGUMENT", "Expected canonical minor-unit string");
  return BigInt(value);
}
function write(value) {
  if (value > MAX || value < -MAX) fail("SDK_RANGE", "Money exceeds 24 digits");
  return value.toString();
}
function integer(value, minimum) {
  if (!Number.isSafeInteger(value) || value < minimum) fail("SDK_ARGUMENT", "Expected safe integer");
}
export const apiVersion = 1;
export const money = Object.freeze({
  fromDecimal(value, fractionDigits) {
    if (typeof value !== "string" || value.length > 32 || !/^-?(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(value)) fail("SDK_ARGUMENT", "Invalid decimal money");
    if (!Number.isInteger(fractionDigits) || fractionDigits < 0 || fractionDigits > 4) fail("SDK_ARGUMENT", "Invalid currency precision");
    const negative = value[0] === "-";
    const parts = (negative ? value.slice(1) : value).split(".");
    const fraction = parts.length === 2 ? parts[1] : "";
    if (fraction.length > fractionDigits) fail("SDK_PRECISION", "Too many fraction digits");
    const result = BigInt(parts[0] + fraction.padEnd(fractionDigits, "0"));
    return write(negative ? -result : result);
  },
  ratio(value, numerator, denominator, rounding) {
    const amount = read(value);
    integer(numerator, 0); integer(denominator, 1);
    if (!["floor", "towardZero", "halfAwayFromZero"].includes(rounding)) fail("SDK_ARGUMENT", "Invalid rounding");
    const product = amount * BigInt(numerator), divisor = BigInt(denominator);
    let quotient = product / divisor;
    const remainder = product % divisor;
    if (rounding === "floor" && remainder < 0n) quotient -= 1n;
    if (rounding === "halfAwayFromZero" && (remainder < 0n ? -remainder : remainder) * 2n >= divisor) quotient += product < 0n ? -1n : 1n;
    return write(quotient);
  },
  add(a, b) { return write(read(a) + read(b)); },
  subtract(a, b) { return write(read(a) - read(b)); }
});
function segments(input) {
  if (!Array.isArray(input) || input.length > 512) fail("SDK_ARGUMENT", "Invalid segments");
  let previousEnd = -Number.MAX_SAFE_INTEGER, total = 0;
  for (const item of input) {
    if (!item || typeof item !== "object" || !Number.isSafeInteger(item.startMs) || !Number.isSafeInteger(item.endMs) || item.endMs <= item.startMs || item.startMs < previousEnd) fail("SDK_ARGUMENT", "Segments must be sorted and non-overlapping");
    total += item.endMs - item.startMs;
    if (!Number.isSafeInteger(total)) fail("SDK_RANGE", "Schedule exceeds safe time range");
    previousEnd = item.endMs;
  }
}
export const time = Object.freeze({
  elapsedMs(input, untilMs) {
    segments(input);
    if (!Number.isSafeInteger(untilMs)) fail("SDK_ARGUMENT", "Invalid instant");
    let total = 0;
    for (const item of input) total += Math.max(0, Math.min(untilMs, item.endMs) - item.startMs);
    return total;
  },
  clip(input, startMs, endMs) {
    segments(input);
    if (!Number.isSafeInteger(startMs) || !Number.isSafeInteger(endMs) || startMs > endMs) fail("SDK_ARGUMENT", "Invalid clip range");
    const output = [];
    for (const item of input) {
      const start = Math.max(startMs, item.startMs), end = Math.min(endMs, item.endMs);
      if (end > start) output.push(Object.freeze({startMs: start, endMs: end}));
    }
    return Object.freeze(output);
  }
});
