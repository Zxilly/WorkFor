using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkFor.UI;

namespace WorkFor.FormProbe;

/// <summary>Own-window component tests: no UI Automation, input injection, or product host pipe.</summary>
public partial class App : Application
{
    private Window? _window;
    private readonly List<string> _checks = [];

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var form = new SchemaParameterForm();
        _window = new Window { Title = "WorkFor 原生参数表单测试", Content = new ScrollViewer { Content = form } };
        form.Loaded += (_, _) => Run(form);
        _window.Activate();
    }

    private void Run(SchemaParameterForm form)
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--result");
        var output = index >= 0 && index + 1 < arguments.Length ? arguments[index + 1]
            : Path.Combine(Environment.CurrentDirectory, "out", "ui", "form-probe.json");
        string? failure = null;
        try
        {
            var schema = JsonNode.Parse("""
              {"type":"object","required":["salary"],"properties":{
                "salary":{"type":"string","title":"时薪","description":"测试金额","default":"16.00"},
                "multiplier":{"type":"number","default":1.5},"count":{"type":"integer"},
                "enabled":{"type":"boolean","default":false},"mode":{"enum":["one","two"]}
              }}
              """);
            var original = JsonNode.Parse("""{"salary":"19.75","unknown":{"keep":true}}""")!.AsObject();
            JsonObject? edited = null;
            var edits = 0;
            form.ParametersEdited += result => { edited = result; edits++; };
            Check(form.Configure(schema, original, out _), "simple schema renders");
            var body = (StackPanel)form.Content;
            var groups = body.Children.Cast<StackPanel>().ToArray();
            Check(groups[0].Children.OfType<TextBox>().Single().Text == "19.75"
                && groups[1].Children.OfType<NumberBox>().Any()
                && groups[2].Children.OfType<NumberBox>().Any()
                && groups[3].Children.OfType<ToggleSwitch>().Any()
                && groups[4].Children.OfType<ComboBox>().Any(), "all five native control types exist");
            Check(edits == 0 && !original.ContainsKey("multiplier") && !original.ContainsKey("enabled"),
                "initial rendering emits no edits and writes no defaults");
            var salary = groups[0].Children.OfType<TextBox>().Single();
            salary.Text = "21.50";
            Check(edited?["salary"]?.GetValue<string>() == "21.50" && edited["unknown"]?["keep"]?.GetValue<bool>() == true,
                "native TextBox edits preserve unknown parameters");
            Check(ReferenceEquals(salary, ((StackPanel)body.Children[0]).Children.OfType<TextBox>().Single()),
                "typing keeps the original TextBox instance");
            groups[1].Children.OfType<CheckBox>().Single().IsChecked = true;
            var number = groups[1].Children.OfType<NumberBox>().Single();
            Check(number.Value == 1.5 && edited?.ContainsKey("multiplier") == true,
                "explicit presence uses a declared numeric default");
            number.Value = 2.25;
            Check(edited?["multiplier"]?.GetValue<double>() == 2.25, "NumberBox edits reach the draft");
            groups[3].Children.OfType<CheckBox>().Single().IsChecked = true;
            Check(edited?.ContainsKey("enabled") == true && edited["enabled"]?.GetValue<bool>() == false,
                "explicit false remains distinct from absence");
            groups[3].Children.OfType<ToggleSwitch>().Single().IsOn = true;
            Check(edited?["enabled"]?.GetValue<bool>() == true, "ToggleSwitch edits reach the draft");
            groups[4].Children.OfType<CheckBox>().Single().IsChecked = true;
            groups[4].Children.OfType<ComboBox>().Single().SelectedIndex = 1;
            Check(edited?["mode"]?.GetValue<string>() == "two", "ComboBox primitive enum reaches the draft");
            groups[3].Children.OfType<CheckBox>().Single().IsChecked = false;
            Check(edited?.ContainsKey("enabled") == false, "unsetting optional parameters preserves absence");
            var editsBeforeSync = edits;
            Check(form.UpdateParameters(JsonNode.Parse("""{"salary":"24.00","unknown":{"keep":true}}""")!.AsObject(), out _)
                && salary.Text == "24.00" && ReferenceEquals(salary, ((StackPanel)body.Children[0]).Children.OfType<TextBox>().Single()),
                "advanced JSON synchronization updates existing controls");
            Check(edits == editsBeforeSync, "programmatic JSON synchronization emits no recursive edits");
            groups[1].Children.OfType<CheckBox>().Single().IsChecked = true;
            number.Text = "not-a-number";
            ExpectInvalid(() => form.CommitPendingEdits(), "pending invalid NumberBox text blocks validation/application");
            number.Text = "";
            form.CommitPendingEdits();
            Check(edited?.ContainsKey("multiplier") == false,
                "clearing a NumberBox removes the value instead of submitting its previous value");

            var requiredSchema = schema!.DeepClone().AsObject();
            requiredSchema["required"] = new JsonArray("salary", "count");
            Check(form.Configure(requiredSchema,
                JsonNode.Parse("""{"salary":"19.75","count":3}""")!.AsObject(), out _), "required numeric form renders");
            groups = ((StackPanel)form.Content).Children.Cast<StackPanel>().ToArray();
            var count = groups[2].Children.OfType<NumberBox>().Single();
            count.Text = "3.5";
            ExpectInvalid(() => form.CommitPendingEdits(), "fractional integer input blocks validation/application");
            count.Text = "";
            form.CommitPendingEdits();
            Check(edited?.ContainsKey("count") == false && !count.IsEnabled,
                "cleared required numeric input stays absent for host required validation");
            groups[0].Children.OfType<TextBox>().Single().Text = "";
            Check(edited?["salary"]?.GetValue<string>() == "",
                "cleared required string is submitted as empty rather than retaining the old salary");
            Check(form.Configure(requiredSchema,
                JsonNode.Parse("""{"salary":"19.75","multiplier":2.5,"count":3}""")!.AsObject(), out _),
                "multiple numeric fields render for transactional pending validation");
            groups = ((StackPanel)form.Content).Children.Cast<StackPanel>().ToArray();
            groups[1].Children.OfType<NumberBox>().Single().Text = "";
            count = groups[2].Children.OfType<NumberBox>().Single();
            count.Text = "invalid";
            ExpectInvalid(() => form.CommitPendingEdits(), "a later invalid number rejects the whole pending commit");
            count.Text = "4";
            form.CommitPendingEdits();
            Check(edited?.ContainsKey("multiplier") == false && edited["count"]?.GetValue<double>() == 4,
                "retrying after an invalid number cannot resurrect another cleared numeric value");
            Check(!form.Configure(JsonNode.Parse("""{"type":"object","properties":{"list":{"type":"array"}}}"""),
                original, out var reason) && reason.Length > 0, "complex schema requests JSON fallback");
        }
        catch (Exception error) { failure = error.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failure is null,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            checks = _checks, failure },
            new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() => _window!.Close());
    }

    private void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks.Add(label);
    }

    private void ExpectInvalid(Action action, string label)
    {
        try { action(); }
        catch (InvalidDataException) { _checks.Add(label); return; }
        throw new InvalidOperationException(label);
    }
}
