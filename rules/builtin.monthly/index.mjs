import { money } from "@workfor/sdk";
export default function calculate(ctx) {
  const salary = money.fromDecimal(ctx.parameters.monthlySalary, ctx.currency.fractionDigits);
  const month = ctx.periods.month;
  if (month.scheduledMs === 0) throw new Error("CALENDAR_EMPTY: No scheduled time this month");
  const accrued = money.ratio(salary, month.elapsedScheduledMs, month.scheduledMs, "floor");
  const beforeToday = money.ratio(salary, month.scheduledBeforeDayMs, month.scheduledMs, "floor");
  return {
    apiVersion: 1, currency: ctx.currency.code,
    amountMinor: ctx.activePeriod === "month" ? accrued : money.subtract(accrued, beforeToday),
    ratePerHourMinor: money.ratio(salary, 3600000, month.scheduledMs, "halfAwayFromZero"),
    note: "按本月计划计薪时长分摊；时薪为折算值"
  };
}
