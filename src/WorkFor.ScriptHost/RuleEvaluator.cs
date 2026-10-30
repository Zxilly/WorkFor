using System.Text;
using System.Text.Json;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Native.Error;
using Jint.Runtime;
using Jint.Runtime.Modules;
using WorkFor.Core;

namespace WorkFor.ScriptHost;

public sealed class RuleEvaluator
{
    private static readonly string Sdk = Resource("WorkFor.ScriptHost.sdk-v1.mjs");
    private static readonly string Harness = Resource("WorkFor.ScriptHost.RuntimeHarness.js");
    public EvaluationOutcome Evaluate(string source, WorkForContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (source is null || Encoding.UTF8.GetByteCount(source) > 65536) return EvaluationOutcome.Failed("SCRIPT_LIMIT", "Rule source exceeds 64 KiB.");
            ContextValidator.Validate(context);
            var parsed = new Parser().ParseModule(source);
            ValidateSyntax(parsed);
            using var engine = new Engine(options =>
            {
                options.Strict();
                options.DisableStringCompilation();
                options.MaxStatements(100_000);
                options.LimitMemory(16 * 1024 * 1024);
                options.TimeoutInterval(TimeSpan.FromMilliseconds(100));
                options.CancellationToken(cancellationToken);
                options.LimitRecursion(64);
                options.RegexTimeoutInterval(TimeSpan.FromMilliseconds(20));
                options.EnableModules(new AllowlistedLoader());
            });
            engine.Execute(Harness);
            engine.Modules.Add("@workfor/sdk", Sdk);
            engine.Modules.Add("workfor:user", source);
            var module = engine.Modules.Import("workfor:user");
            var entry = module.Get("default");
            var contextJson = JsonSerializer.Serialize(context, WorkForJson.Options);
            var json = engine.Invoke("__workforRun", entry, contextJson).AsString();
            if (Encoding.UTF8.GetByteCount(json) > 8192) return EvaluationOutcome.Failed("RESULT_INVALID", "Result exceeds 8 KiB.");
            var result = JsonSerializer.Deserialize<RuleResult>(json, WorkForJson.Options);
            if (result is null || result.ApiVersion != 1 || !MoneyFormatter.IsCanonical(result.AmountMinor)) return EvaluationOutcome.Failed("RESULT_INVALID", "Invalid rule result.");
            if (result.Currency != context.Currency.Code) return EvaluationOutcome.Failed("CURRENCY_MISMATCH", "Result currency differs from context.");
            return new(result, null);
        }
        catch (PolicyException ex) { return EvaluationOutcome.Failed(ex.Code, ex.Message); }
        catch (ParseErrorException ex) { return new(null, new("SCRIPT_SYNTAX", ex.Description, "index.mjs", ex.LineNumber, ex.Column + 1)); }
        catch (JavaScriptException ex)
        {
            // Do not run a user-provided toString/getter while rendering a thrown value.
            var message = ex.Error.IsString() ? ex.Error.AsString() : "Rule threw an exception.";
            if (ex.Error is ErrorInstance error)
            {
                var descriptor = error.GetOwnProperty("message");
                if (descriptor.Value?.IsString() == true) message = descriptor.Value.AsString();
            }
            var code = KnownError(message);
            return EvaluationOutcome.Failed(code, message.Length > 500 ? message[..500] : message);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Timeout", StringComparison.Ordinal) || ex is OperationCanceledException)
        { return EvaluationOutcome.Failed("SCRIPT_TIMEOUT", "Rule execution timed out.", true); }
        catch (Exception ex) when (ex.GetType().Name.Contains("Statements", StringComparison.Ordinal) || ex.GetType().Name.Contains("MemoryLimit", StringComparison.Ordinal) || ex.GetType().Name.Contains("Recursion", StringComparison.Ordinal))
        { return EvaluationOutcome.Failed("SCRIPT_LIMIT", "Rule execution exceeded resource limits."); }
        catch (ArgumentException ex) { return EvaluationOutcome.Failed("CONTEXT_INVALID", ex.Message); }
        catch (JsonException) { return EvaluationOutcome.Failed("RESULT_INVALID", "Malformed JSON data."); }
        catch (Exception ex) { return EvaluationOutcome.Failed("SCRIPT_EXCEPTION", "Rule failed: " + ex.GetType().Name); }
    }

    private static void ValidateSyntax(Node root)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is ImportDeclaration import && import.Source.Value is not "@workfor/sdk") throw new PolicyException("MODULE_NOT_ALLOWED", "Only @workfor/sdk may be imported.");
            if (node.Type.ToString() is "ImportExpression" or "AwaitExpression") throw new PolicyException("ASYNC_NOT_SUPPORTED", "Dynamic import and await are not supported.");
            if (node is ExportAllDeclaration || node is ExportNamedDeclaration { Source: not null }) throw new PolicyException("MODULE_NOT_ALLOWED", "Re-exporting modules is not supported.");
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
    }

    private static string KnownError(string message)
    {
        foreach (var code in new[] { "SDK_ARGUMENT", "SDK_PRECISION", "SDK_RANGE", "ASYNC_NOT_SUPPORTED", "RESULT_INVALID", "CURRENCY_MISMATCH", "API_VERSION_UNSUPPORTED", "SCRIPT_ENTRY_INVALID", "MODULE_NOT_ALLOWED" })
            if (message.Contains(code + ":", StringComparison.Ordinal)) return code;
        return "SCRIPT_EXCEPTION";
    }
    private static string Resource(string name)
    {
        using var stream = typeof(RuleEvaluator).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded runtime asset.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private sealed class PolicyException(string code, string message) : Exception(message) { public string Code { get; } = code; }
    private sealed class AllowlistedLoader : IModuleLoader
    {
        public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest request)
        {
            if (request.Specifier is not ("@workfor/sdk" or "workfor:user") || request.Attributes.Length != 0) throw new PolicyException("MODULE_NOT_ALLOWED", "Module is not allowed.");
            return new(request, request.Specifier, null, SpecifierType.Bare);
        }
        public Jint.Runtime.Modules.Module LoadModule(Engine engine, ResolvedSpecifier resolved) => throw new PolicyException("MODULE_NOT_ALLOWED", "External module loading is disabled.");
    }
}
