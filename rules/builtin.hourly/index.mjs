import { money } from "@workfor/sdk";
export default function calculate(ctx) {
  const rate = money.fromDecimal(ctx.parameters.hourlyRate, ctx.currency.fractionDigits);
  return {
    apiVersion: 1, currency: ctx.currency.code,
    amountMinor: money.ratio(rate, ctx.periods[ctx.activePeriod].elapsedScheduledMs, 3600000, "floor"),
    ratePerHourMinor: rate
  };
}
