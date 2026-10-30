/** WorkFor script API v1 — design contract, implementation pending. */
declare module "@workfor/sdk" {
  export const apiVersion: 1;
  export type JsonPrimitive = string | number | boolean | null;
  export type JsonValue = JsonPrimitive | readonly JsonValue[] | {
    readonly [key: string]: JsonValue;
  };
  /** Canonical signed base-10 integer, no leading zero, at most 24 digits. */
  export type MinorUnits = string;
  export type Rounding = "floor" | "towardZero" | "halfAwayFromZero";
  export type PeriodKind = "day" | "month";
  export interface CurrencyInfo {
    readonly code: string;
    readonly fractionDigits: 0 | 1 | 2 | 3 | 4;
  }
  export interface Segment {
    readonly startMs: number;
    readonly endMs: number;
  }
  export interface PeriodMetrics {
    readonly kind: PeriodKind;
    readonly key: string;
    readonly startMs: number;
    readonly endMs: number;
    readonly scheduledMs: number;
    readonly elapsedScheduledMs: number;
    readonly elapsedWallMs: number;
  }
  export interface MonthMetrics extends PeriodMetrics {
    readonly kind: "month";
    readonly scheduledBeforeDayMs: number;
  }
  export interface DayMetrics extends PeriodMetrics {
    readonly kind: "day";
  }
  export interface WorkForContext {
    readonly apiVersion: 1;
    readonly evaluationId: string;
    readonly configRevision: number;
    readonly calendarRevision: number;
    readonly now: {
      readonly epochMs: number;
      readonly timeZone: string;
      readonly localDate: string;
      readonly localTime: string;
      readonly isoWeekday: 1 | 2 | 3 | 4 | 5 | 6 | 7;
      readonly utcOffsetMinutes: number;
    };
    readonly activePeriod: PeriodKind;
    readonly currency: CurrencyInfo;
    readonly parameters: { readonly [key: string]: JsonValue };
    readonly periods: {
      readonly day: DayMetrics;
      readonly month: MonthMetrics;
    };
    readonly schedule: { readonly segments: readonly Segment[] };
  }
  export interface RuleResult {
    apiVersion: 1;
    currency: string;
    amountMinor: MinorUnits;
    ratePerHourMinor?: MinorUnits;
    note?: string;
    metrics?: Array<{ label: string; value: string }>;
  }
  export type Calculate = (context: WorkForContext) => RuleResult;
  export const money: Readonly<{
    fromDecimal(value: string, fractionDigits: number): MinorUnits;
    ratio(value: MinorUnits, numerator: number, denominator: number,
      rounding: Rounding): MinorUnits;
    add(a: MinorUnits, b: MinorUnits): MinorUnits;
    subtract(a: MinorUnits, b: MinorUnits): MinorUnits;
  }>;
  export const time: Readonly<{
    elapsedMs(segments: readonly Segment[], untilMs: number): number;
    clip(segments: readonly Segment[], startMs: number,
      endMs: number): readonly Segment[];
  }>;
}
