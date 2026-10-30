using System.Text.Json;
using WorkFor.Core;
using Xunit;

namespace WorkFor.ScriptHost.Tests;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator evaluator = new();
    private static WorkForContext Context(string instant = "2026-10-30T11:00:00+08:00", string period = "day") => CalendarContextBuilder.Build(DateTimeOffset.Parse(instant), CalendarSettings.Default, CurrencyCatalog.Get("CNY"), JsonSerializer.SerializeToElement(new { hourlyRate="16.00", monthlySalary="16000.00" }), period);
    private EvaluationOutcome Run(string body) => evaluator.Evaluate("import { money, time } from '@workfor/sdk'; export default function(ctx) { " + body + " }", Context());
    private static string Return(string expression) => "return {apiVersion:1,currency:'CNY',amountMinor:" + expression + "};";

    [Fact]
    public void HourlyRuleEvaluatesInRealJint()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"rules","builtin.hourly","index.mjs"));
        var result = evaluator.Evaluate(source, Context(), TestContext.Current.CancellationToken);
        Assert.Null(result.Error); Assert.Equal("3200", result.Result?.AmountMinor);
    }

    [Theory]
    [InlineData("money.ratio('-3',1,2,'floor')", "-2")]
    [InlineData("money.ratio('-3',1,2,'towardZero')", "-1")]
    [InlineData("money.ratio('-3',1,2,'halfAwayFromZero')", "-2")]
    [InlineData("money.fromDecimal('-0.00',2)", "0")]
    [InlineData("money.fromDecimal('12.860',3)", "12860")]
    [InlineData("money.ratio('999999999999999999999999',9007199254740991,9007199254740991,'floor')", "999999999999999999999999")]
    [InlineData("money.add('999999999999999999999998','1')", "999999999999999999999999")]
    public void SdkMoneyKeepsExactPrecision(string expression, string expected)
    { var result=Run(Return(expression)); Assert.Null(result.Error); Assert.Equal(expected,result.Result?.AmountMinor); }

    [Theory]
    [InlineData("money.fromDecimal('1.005',2)","SDK_PRECISION")]
    [InlineData("money.ratio('10',1,0,'floor')","SDK_ARGUMENT")]
    [InlineData("money.add('999999999999999999999999','1')","SDK_RANGE")]
    [InlineData("money.fromDecimal('1e3',2)","SDK_ARGUMENT")]
    public void SdkRejectsInvalidInputs(string expression,string code) => Assert.Equal(code,Run(Return(expression)).Error?.Code);

    [Fact]
    public void NoStateSurvivesAcrossInvocations()
    {
        const string source="let counter=0; export default ctx=>({apiVersion:1,currency:'CNY',amountMinor:String(++counter)});";
        Assert.Equal("1",evaluator.Evaluate(source,Context(), TestContext.Current.CancellationToken).Result?.AmountMinor);
        Assert.Equal("1",evaluator.Evaluate(source,Context(), TestContext.Current.CancellationToken).Result?.AmountMinor);
    }

    [Theory]
    [InlineData("import x from './secret.mjs'; export default ()=>0;","MODULE_NOT_ALLOWED")]
    [InlineData("export * from '@workfor/sdk'; export default ()=>0;","MODULE_NOT_ALLOWED")]
    [InlineData("export default ()=>import('@workfor/sdk');","ASYNC_NOT_SUPPORTED")]
    [InlineData("export default async()=>({apiVersion:1,currency:'CNY',amountMinor:'0'});","ASYNC_NOT_SUPPORTED")]
    public void RejectsModulesAndAsync(string source,string code) => Assert.Equal(code,evaluator.Evaluate(source,Context(), TestContext.Current.CancellationToken).Error?.Code);

    [Theory]
    [InlineData("return {apiVersion:1,currency:'USD',amountMinor:'0'};","CURRENCY_MISMATCH")]
    [InlineData("return {apiVersion:1,currency:'CNY',amountMinor:'01'};","RESULT_INVALID")]
    [InlineData("return {apiVersion:1,currency:'CNY',get amountMinor(){while(true){}}};","RESULT_INVALID")]
    [InlineData("const result={apiVersion:1,currency:'CNY',amountMinor:'0'};result.extra=result;return result;","RESULT_INVALID")]
    [InlineData("return {apiVersion:1,currency:'CNY',amountMinor:1n};","RESULT_INVALID")]
    [InlineData("ctx.parameters.hourlyRate='100';return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("Date.now();return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("new Date();return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("Math.random();return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("eval('1');return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("globalThis.BigInt=()=>0n;return {apiVersion:1,currency:'CNY',amountMinor:'0'};","SCRIPT_EXCEPTION")]
    [InlineData("return {apiVersion:1,currency:'CNY',amountMinor:'0',[Symbol('secret')]:1n};","RESULT_INVALID")]
    public void EnforcesOutputAndDeterminismRules(string body,string code) => Assert.Equal(code,Run(body).Error?.Code);

    [Fact]
    public void BrowserNodeAndClrCapabilitiesAreAbsent()
    {
        var outcome=Run("if (typeof fetch!=='undefined'||typeof process!=='undefined'||typeof require!=='undefined'||typeof System!=='undefined'||typeof importNamespace!=='undefined'||typeof setTimeout!=='undefined') throw new Error('capability leak');"+Return("'0'"));
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ThrownUserObjectDoesNotExecuteToStringDuringErrorRendering()
    {
        var result=Run("throw {toString(){while(true){}}};");
        Assert.Equal("SCRIPT_EXCEPTION",result.Error?.Code);
        Assert.Equal("Rule threw an exception.",result.Error?.Message);
    }

    [Fact]
    public void SegmentsSdkClipsWithoutDoubleCounting()
    {
        var result=Run(Return("String(time.elapsedMs(time.clip([{startMs:1000,endMs:5000},{startMs:8000,endMs:12000}],2000,11000),10000))"));
        Assert.Equal("5000",result.Result?.AmountMinor);
    }

    [Fact]
    public void InfiniteLoopIsBoundedAndNextEvaluationRecovers()
    {
        var result=Run("while(true){} ");
        Assert.Contains(result.Error?.Code,new[]{"SCRIPT_LIMIT","SCRIPT_TIMEOUT"});
        Assert.Equal("10",Run(Return("'10'")).Result?.AmountMinor);
    }

    [Fact]
    public void MonthlyDailyDifferencesTelescopeToFullSalary()
    {
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"rules","builtin.monthly","index.mjs"));
        System.Numerics.BigInteger sum=0;
        for(var day=1;day<=31;day++)
        {
            var result=evaluator.Evaluate(source,Context($"2026-10-{day:D2}T23:59:59+08:00"), TestContext.Current.CancellationToken);
            Assert.Null(result.Error);sum+=System.Numerics.BigInteger.Parse(result.Result!.AmountMinor);
        }
        Assert.Equal(1600000,sum);
        Assert.Equal("1600000",evaluator.Evaluate(source,Context("2026-10-31T23:59:59+08:00","month"), TestContext.Current.CancellationToken).Result?.AmountMinor);
    }
}
