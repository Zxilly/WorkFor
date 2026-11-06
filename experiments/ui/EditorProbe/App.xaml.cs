using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.UI;

namespace WorkFor.EditorProbe;

public partial class App : Application
{
    private readonly List<string> _checks = [];
    private Window? _window;
    private StackPanel _body = null!;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) => WriteResult(error.Exception.ToString());
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _body = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        CodeEditor json, script;
        try
        {
            json = new CodeEditor("计薪时段", "shifts", 160) { Text = "[{\"start\":\"09:00\",\"end\":\"12:00\"}]" };
            script = new CodeEditor("JavaScript", "script", 220)
            {
                Text = "import { money } from '@workfor/sdk';\nexport default function calculate(ctx) {\n  ctx.\n}",
                ParametersSchema = JsonNode.Parse("""{"type":"object","required":["hourlyRate"],"properties":{"hourlyRate":{"type":"string"}}}""")
            };
        }
        catch (Exception error) { WriteResult(error.ToString()); Exit(); return; }
        _body.Children.Add(json); _body.Children.Add(script);
        _window = new Window { Title = "WorkFor 代码编辑器验证", Content = new ScrollViewer { Content = _body } };
        WindowLayout.Configure(_window, 980, 780, compact: false);
        _window.Activate();
        string? failure = null;
        try
        {
            await Ready(json); await Ready(script);
            Check(true, "offline WebView2 and both stdio language servers connected");
            var inspection = await Evaluate(json, "window.workforEditor.inspect()");
            Check(inspection!["tokens"]!.AsArray().Any(line => line!.AsArray().Any(token => token!["type"]!.ToString().Contains("string"))), "JSON syntax tokens exist");
            var completions = await Evaluate(script, "window.workforEditor.request('textDocument/completion',{position:{line:2,character:6}})");
            Check(Labels(completions).Contains("periods") && Labels(completions).Contains("parameters"), "LSP completes typed ctx members without modifying the saved script");
            await Evaluate(script, "window.workforEditor.suggest(3,7)");
            await Until(async () => (await Evaluate(script, "document.querySelector('.suggest-widget')?.textContent"))?.ToString().Contains("activePeriod") == true);
            Check(true, "Monaco suggestion widget displays actual language-server completions");
            script.Text = "import { money } from '@workfor/sdk';\nexport default function calculate(ctx) {\n  ctx.parameters.\n}";
            await Updated(script);
            completions = await Evaluate(script, "window.workforEditor.request('textDocument/completion',{position:{line:2,character:17}})");
            Check(Labels(completions).Contains("hourlyRate"), "LSP completes current rule parameters from its schema");
            script.Text = "import { money } from '@workfor/sdk';\nexport default function calculate(ctx) {\n  money.\n}";
            await Updated(script);
            completions = await Evaluate(script, "window.workforEditor.request('textDocument/completion',{position:{line:2,character:8}})");
            Check(Labels(completions).Contains("fromDecimal") && Labels(completions).Contains("ratio"), "LSP resolves @workfor/sdk imports");
            await script.FlushAsync(); Check(!script.Text.Contains("@type"), "analysis-only parameter types never enter the draft");
            json.Text = "[{\n  \"\"\n}]";
            await Updated(json);
            completions = await Evaluate(json, "window.workforEditor.request('textDocument/completion',{position:{line:1,character:3}})");
            Check(Labels(completions).Any(label => label.Contains("start")), "JSON LSP supplies calendar schema fields");
            json.Text = "[{\"start\":5}]"; await Updated(json);
            await Until(async () => (await Evaluate(json, "window.workforEditor.inspect()"))?["markers"]?.AsArray().Count > 0);
            Check(true, "JSON schema diagnostics reach Monaco markers");
            json.Text = "[{\"start\":\"09:00\",\"end\":\"12:00\"}]"; await Updated(json);
            await json.FormatAsync(); await json.FlushAsync();
            Check(json.Text.Contains('\n') && JsonNode.DeepEquals(JsonNode.Parse(json.Text), JsonNode.Parse("[{\"start\":\"09:00\",\"end\":\"12:00\"}]")), "LSP formatting preserves JSON values and reaches the native draft");
            _body.RequestedTheme = ElementTheme.Dark;
            await Until(async () => (await Evaluate(json, "window.workforEditor.inspect()"))?["theme"]?.ToString().Contains("vs-dark") == true);
            Check(true, "editor follows native dark theme");
            var draft = json.Text; _body.Children.Remove(json); _body.Children.Insert(0, json);
            await Task.Delay(200); await json.FlushAsync(); Check(json.Text == draft, "visual-tree reattachment preserves the draft");
            await Evaluate(json, "window.workforEditor.configure({...window.workforEditor.getState(),kind:'shifts',value:'outdated',readOnly:true})");
            await json.FlushAsync(); Check(json.Text == draft, "same-revision policy updates cannot overwrite recent browser edits");
            await Evaluate(json, "window.chrome.webview.postMessage({type:'changed',revision:-1,value:'stale'})"); await Task.Delay(100);
            Check(json.Text == draft, "stale browser revisions cannot replace a newer draft");
        }
        catch (Exception error)
        {
            failure = error.ToString();
            try { failure += "\n" + (await Evaluate(script, "({inspection:window.workforEditor.inspect(),widget:document.querySelector('.suggest-widget')?.textContent})"))?.ToJsonString(); } catch (Exception) { }
        }
        WriteResult(failure);
        json.Dispose(); script.Dispose(); _window.Close();
    }
    private void WriteResult(string? failure)
    {
        var arguments = Environment.GetCommandLineArgs(); var index = Array.IndexOf(arguments, "--result");
        var output = index >= 0 ? arguments[index + 1] : Path.Combine(Environment.CurrentDirectory, "out/ui/editor-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failure is null, checks = _checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }
    private static WebView2 View(CodeEditor editor) => ((Grid)((Border)((StackPanel)editor.Content).Children[1]).Child).Children.OfType<WebView2>().Single();
    private static async Task<JsonNode?> Evaluate(CodeEditor editor, string expression)
    {
        var result = JsonNode.Parse(await View(editor).CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", new JsonObject
        { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true }.ToJsonString()))!;
        if (result["exceptionDetails"] is { } error) throw new InvalidOperationException(error.ToJsonString());
        return result["result"]?["value"];
    }
    private static Task Ready(CodeEditor editor) => Until(async () =>
    {
        try { return (await Evaluate(editor, "window.workforEditor?.inspect()"))?["lspReady"]?.GetValue<bool>() == true; }
        catch (NullReferenceException) { return false; }
    });
    private static Task Updated(CodeEditor editor) => Until(async () => (await Evaluate(editor, "window.workforEditor.getState()"))?["value"]?.GetValue<string>() == editor.Text);
    private static IEnumerable<string> Labels(JsonNode? result) => (result is JsonArray array ? array : result?["items"]?.AsArray() ?? []).Select(item => item?["label"]?.ToString() ?? "");
    private static async Task Until(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(100); }
        throw new TimeoutException("Editor check timed out.");
    }
    private void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException(label); _checks.Add(label); }
}
