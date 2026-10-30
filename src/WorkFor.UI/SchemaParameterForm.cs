using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace WorkFor.UI;

/// <summary>Native flat-schema controls; editing a value does not recreate controls or steal focus.</summary>
internal sealed class SchemaParameterForm : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 20 };
    private UIElementCollection Children => _body.Children;
    private readonly Dictionary<string, FieldControl> _controls = new(StringComparer.Ordinal);
    private ParameterFormModel? _model;
    private bool _updating;
    public event Action<JsonObject>? ParametersEdited;

    public SchemaParameterForm() => Content = _body;

    public bool Configure(JsonNode? schema, JsonObject parameters, out string reason)
    {
        if (!ParameterFormModel.TryCreate(schema, parameters, out var model, out reason))
        {
            _model = null;
            Children.Clear();
            _controls.Clear();
            return false;
        }
        _updating = true;
        try
        {
            _model = model;
            Children.Clear();
            _controls.Clear();
            foreach (var field in model!.Fields)
            {
                var group = new StackPanel { Spacing = 8 };
                group.Children.Add(WindowLayout.Label(field.Title + (field.Required ? "（必填）" : "")));
                if (field.Description.Length > 0)
                {
                    var description = WindowLayout.Label(field.Description, "CaptionTextBlockStyle");
                    description.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
                    group.Children.Add(description);
                }
                var presence = new CheckBox { Content = "填写此参数", IsChecked = model.Contains(field.Name) };
                var editor = Editor(field);
                AutomationProperties.SetName(editor, field.Title + (field.Required ? "，必填" : "，可选"));
                var missing = WindowLayout.Label(field.HasDefault
                    ? $"尚未填写。可选择使用默认值：{ParameterFormModel.ChoiceLabel(field.DefaultValue)}"
                    : "尚未填写；填写后才会提交此参数。", "CaptionTextBlockStyle");
                missing.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
                group.Children.Add(presence);
                group.Children.Add(editor);
                group.Children.Add(missing);
                var entry = new FieldControl(presence, editor, missing);
                _controls.Add(field.Name, entry);
                if (field.HasDefault)
                {
                    var useDefault = new Button { Content = "使用默认值" };
                    AutomationProperties.SetName(useDefault, $"{field.Title}：使用默认值");
                    useDefault.Click += (_, _) =>
                    {
                        if (_updating || _model is null) return;
                        _model.UseDefault(field.Name);
                        RefreshField(field);
                        Publish();
                    };
                    group.Children.Add(useDefault);
                }
                presence.Checked += (_, _) =>
                {
                    if (_updating || _model is null) return;
                    if (!_model.Contains(field.Name)) _model.SetValue(field.Name, _model.InitialValue(field.Name));
                    RefreshField(field);
                    Publish();
                };
                presence.Unchecked += (_, _) =>
                {
                    if (_updating || _model is null) return;
                    _model.Remove(field.Name);
                    RefreshField(field);
                    Publish();
                };
                Children.Add(group);
                RefreshField(field);
            }
            if (model.Fields.Count == 0) Children.Add(WindowLayout.Label("此规则没有需要填写的参数。"));
        }
        finally { _updating = false; }
        return true;
    }

    public bool UpdateParameters(JsonObject parameters, out string reason)
    {
        reason = "";
        if (_model is null) return false;
        try { _model.ReplaceParameters(parameters); }
        catch (InvalidDataException error) { reason = error.Message; return false; }
        foreach (var field in _model.Fields) RefreshField(field);
        return true;
    }

    private Control Editor(ParameterField field)
    {
        switch (field.Kind)
        {
            case ParameterFieldKind.String:
                var text = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = field.HasDefault ? ParameterFormModel.ChoiceLabel(field.DefaultValue) : "" };
                // TextChanging is synchronous; queued TextChanged events could otherwise look like
                // user edits after an initial/programmatic render has left its suppression scope.
                text.TextChanging += (_, _) => Commit(field, JsonValue.Create(text.Text));
                return text;
            case ParameterFieldKind.Number:
            case ParameterFieldKind.Integer:
                var number = new NumberBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    SmallChange = field.Kind == ParameterFieldKind.Integer ? 1 : 0.1,
                    ValidationMode = NumberBoxValidationMode.Disabled,
                    PlaceholderText = field.HasDefault ? ParameterFormModel.ChoiceLabel(field.DefaultValue) : "" };
                number.ValueChanged += (_, _) =>
                {
                    if (double.IsFinite(number.Value)) Commit(field, JsonValue.Create(number.Value));
                };
                return number;
            case ParameterFieldKind.Boolean:
                var toggle = new ToggleSwitch { OnContent = "开", OffContent = "关" };
                toggle.Toggled += (_, _) => Commit(field, JsonValue.Create(toggle.IsOn));
                return toggle;
            default:
                var combo = new ComboBox { MinWidth = 260, PlaceholderText = "请选择" };
                foreach (var choice in field.Choices) combo.Items.Add(new ComboBoxItem
                    { Content = ParameterFormModel.ChoiceLabel(choice) });
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0) Commit(field, field.Choices[combo.SelectedIndex]);
                };
                return combo;
        }
    }

    /// <summary>Never submit a stale JSON value while a NumberBox has pending invalid/empty text.</summary>
    public void CommitPendingEdits()
    {
        if (_model is null) return;
        var pending = new List<(ParameterField Field, double? Value)>();
        var changed = false;
        foreach (var field in _model.Fields)
        {
            if (!_model.Contains(field.Name) || _controls[field.Name].Editor is not NumberBox number) continue;
            var text = number.Text.Trim();
            if (text.Length == 0)
            {
                pending.Add((field, null));
                continue;
            }
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.CurrentCulture, out var value)
                || !double.IsFinite(value) || Math.Abs(value) > 9_007_199_254_740_991d)
                throw new InvalidDataException($"参数“{field.Title}”不是可用数字，请修正后再验证或应用。");
            if (field.Kind == ParameterFieldKind.Integer && Math.Truncate(value) != value)
                throw new InvalidDataException($"参数“{field.Title}”必须是整数。");
            pending.Add((field, value));
        }
        // Check every pending field before changing the model. A later invalid field must not leave
        // an earlier cleared field only removed internally while the advanced JSON still has it.
        foreach (var (field, value) in pending)
        {
            if (value is null)
            {
                _model.Remove(field.Name);
                changed = true;
            }
            else if (!ParameterFormModel.TryNumber(_model.Value(field.Name), out var oldValue) || oldValue != value.Value)
            {
                _model.SetValue(field.Name, JsonValue.Create(value.Value));
                changed = true;
            }
            RefreshField(field);
        }
        if (changed) Publish();
    }

    private void Commit(ParameterField field, JsonNode? value)
    {
        if (_updating || _model is null || !_model.Contains(field.Name)) return;
        _model.SetValue(field.Name, value);
        Publish();
    }

    private void RefreshField(ParameterField field)
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            var entry = _controls[field.Name];
            var hasValue = _model!.Contains(field.Name);
            var value = _model.Value(field.Name);
            // Existing required fields are simply editable; missing / optional fields keep explicit presence.
            entry.Presence.Visibility = field.Required && hasValue ? Visibility.Collapsed : Visibility.Visible;
            entry.Presence.IsChecked = hasValue;
            entry.Editor.IsEnabled = hasValue;
            entry.Missing.Visibility = hasValue ? Visibility.Collapsed : Visibility.Visible;
            switch (entry.Editor)
            {
                case TextBox text: text.Text = hasValue ? value!.GetValue<string>() : ""; break;
                case NumberBox number:
                    number.Value = hasValue && ParameterFormModel.TryNumber(value, out var numeric) ? numeric : double.NaN;
                    break;
                case ToggleSwitch toggle: toggle.IsOn = hasValue && value!.GetValue<bool>(); break;
                case ComboBox combo:
                    combo.SelectedIndex = hasValue ? field.Choices.ToList().FindIndex(choice => ParameterFormModel.SameValue(choice, value)) : -1;
                    break;
            }
        }
        finally { _updating = wasUpdating; }
    }

    private void Publish() => ParametersEdited?.Invoke(_model!.Snapshot());
    private sealed record FieldControl(CheckBox Presence, Control Editor, TextBlock Missing);
}
