using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkFor.UI;

internal sealed class SettingsWindow : Window
{
    private readonly HostClient _client = new();
    private readonly NavigationView _navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        PaneTitle = "WorkFor"
    };
    private readonly StackPanel _page = new() { Padding = new Thickness(32), Spacing = 20, MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Left };
    private readonly InfoBar _feedback = new() { IsClosable = true };
    private readonly TextBlock _connection = WindowLayout.Label("正在连接计算宿主", "CaptionTextBlockStyle");
    private readonly ComboBox _period = new() { Header = "展示范围", MinWidth = 260 };
    private readonly ComboBox _rulePicker = new() { Header = "计算方式", MinWidth = 300 };
    private readonly TextBox _currency = new() { Header = "币种", MaxLength = 3, Width = 260,
        HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _parameters = JsonEditor("规则参数（JSON）", 120);
    private readonly SchemaParameterForm _parameterForm = new();
    private readonly TextBlock _parameterFallback = WindowLayout.Label("", "CaptionTextBlockStyle");
    private readonly Expander _parametersExpander;
    private readonly TextBox _source = JsonEditor("JavaScript 规则", 280);
    private readonly TextBox _manifest = JsonEditor("规则清单与参数校验（JSON）", 240);
    private readonly Expander _manifestExpander;
    private readonly TextBox _timeZone = new() { Header = "时区", Text = "Asia/Shanghai", MinWidth = 300 };
    private readonly TextBox _weekdays = new() { Header = "计薪工作日", PlaceholderText = "1,2,3,4,5",
        Description = "周一为 1，周日为 7；多个日期用英文逗号分隔。", MinWidth = 300 };
    private readonly TextBox _shifts = JsonEditor("计薪时段（JSON）", 160);
    private readonly TextBox _dateOverrides = JsonEditor("日期覆盖（JSON）", 180);
    private readonly NumberBox _width = new() { Header = "任务栏请求宽度（DIP）", Minimum = 180, Maximum = 360,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 300,
        HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleSwitch _startWithWindows = new() { Header = "登录时启动", OnContent = "开", OffContent = "关" };
    private readonly TextBlock _preview = WindowLayout.Label("验证规则后可预览当前时刻的计算结果。");
    private readonly TextBlock _liveSnapshot = WindowLayout.Label("等待首次计算。", "CaptionTextBlockStyle");
    private readonly TextBlock _diagnostics = WindowLayout.Label("等待宿主诊断信息。");
    private readonly List<Button> _actions = [];
    private readonly NavigationViewItem _exitItem = new()
    {
        Content = "退出 WorkFor", Tag = "exit", Icon = new FontIcon { Glyph = "\uE7E8" }
    };
    private JsonObject? _config;
    private bool _busy;
    private bool _closed;
    private bool _populating;
    private IReadOnlyList<RuleCatalogItem> _catalog = [];
    private string _section = "rules";
    private bool _syncingParameters;
    private string? _parameterSchemaKey;

    public SettingsWindow()
    {
        Title = "WorkFor 设置";
        SystemBackdrop = new MicaBackdrop();
        _source.AcceptsReturn = true;
        _source.PlaceholderText = "可粘贴自定义入口脚本；留空使用当前规则。";
        _shifts.Description = "例如 [{\"start\":\"09:00\",\"end\":\"18:00\"}]。全天可用 start/end 均为 00:00 并设 endNextDay 为 true。";
        _dateOverrides.Description = "用日期覆盖当日的计薪计划。例如 {\"2026-10-01\": []} 表示当天不计薪；值也可填写计薪时段数组。";
        _parameterFallback.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
        _parametersExpander = new Expander
        {
            Header = "高级：参数 JSON",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _parameters
        };
        _parameterForm.ParametersEdited += parameters =>
        {
            _syncingParameters = true;
            try { _parameters.Text = parameters.ToJsonString(PrettyJson); }
            finally { _syncingParameters = false; }
        };
        _parameters.TextChanged += (_, _) =>
        {
            if (!_populating && !_syncingParameters) RefreshParameterForm();
        };
        _manifest.TextChanged += (_, _) => { if (!_populating) RefreshParameterForm(); };
        _rulePicker.SelectionChanged += (_, _) =>
        {
            if (_populating || _rulePicker.SelectedItem is not ComboBoxItem { Tag: RuleCatalogItem rule }) return;
            _populating = true;
            try
            {
                _source.Text = rule.Source;
                _manifest.Text = rule.Manifest.ToJsonString(PrettyJson);
                _parameters.Text = rule.DefaultParameters.ToJsonString(PrettyJson);
            }
            finally { _populating = false; }
            RefreshParameterForm();
            _preview.Text = "已载入候选规则。验证并应用后才会改变当前计算。";
        };
        _manifestExpander = new Expander
        {
            Header = "高级：规则清单与参数 Schema",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _manifest
        };
        _period.Items.Add(new ComboBoxItem { Content = "今日收入", Tag = "day" });
        _period.Items.Add(new ComboBoxItem { Content = "本月收入", Tag = "month" });
        _period.SelectedIndex = 0;
        foreach (var (title, tag, icon) in new[]
        {
            ("收入规则", "rules", Symbol.Calculator), ("计薪日历", "calendar", Symbol.Calendar),
            ("任务栏", "taskbar", Symbol.View), ("诊断", "diagnostics", Symbol.Help)
        }) _navigation.MenuItems.Add(new NavigationViewItem { Content = title, Tag = tag, Icon = new SymbolIcon(icon) });
        _navigation.FooterMenuItems.Add(_exitItem);

        _navigation.SelectionChanged += (sender, args) =>
        {
            if (args.SelectedItemContainer?.Tag is not string section) return;
            if (section == "exit") { _ = RunAsync(ShutdownAsync); return; }
            ShowSection(section);
        };
        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _feedback.Margin = new Thickness(32, 16, 32, 0);
        frame.Children.Add(_feedback);
        var scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        frame.Children.Add(scroll);
        _connection.Margin = new Thickness(32, 8, 32, 16);
        _connection.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
        Grid.SetRow(_connection, 2);
        frame.Children.Add(_connection);
        _navigation.Content = frame;
        Content = _navigation;
        WindowLayout.Configure(this, 1000, 780, compact: false);
        _client.ConnectionChanged += state => DispatcherQueue.TryEnqueue(() => _connection.Text =
            state.Length > 0 ? state : "已连接 · 由计算宿主每秒更新");
        _client.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            _liveSnapshot.Text = $"当前显示：{snapshot.Title} {snapshot.AmountText} · {snapshot.StatusText}";
            if (_config is null && !_busy) _ = LoadConfigurationAsync();
        });
        Closed += async (_, _) => { _closed = true; await _client.DisposeAsync(); };
        _navigation.SelectedItem = _navigation.MenuItems[0];
        _ = LoadConfigurationAsync();
    }

    private static TextBox JsonEditor(string header, double height) => new()
    {
        Header = header, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"), MinHeight = height, MaxHeight = 420,
        HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 440
    };

    private void ShowSection(string section)
    {
        _section = section;
        // Unparent controls before moving reusable editors into the new section.
        _page.Children.Clear();
        _actions.Clear();
        switch (section)
        {
            case "calendar":
                Heading("计薪日历", "金额根据当前时刻和计薪计划计算，睡眠或锁屏不会暂停计薪。");
                Add(_timeZone, _weekdays, _shifts, _dateOverrides);
                _page.Children.Add(Action("应用日历", ApplyAsync, primary: true));
                break;
            case "taskbar":
                Heading("任务栏", "收入显示与系统任务栏使用相同的主题和缩放。原生适配状态见诊断。");
                Add(_width, _startWithWindows);
                _page.Children.Add(Action("应用任务栏设置", ApplyAsync, primary: true));
                break;
            case "diagnostics":
                Heading("诊断", "连接、规则和原生任务栏组件的状态均来自后台宿主。");
                _page.Children.Add(_diagnostics);
                _page.Children.Add(Action("读取诊断信息", LoadDiagnosticsAsync));
                _page.Children.Add(Action("重新连接任务栏", async () =>
                {
                    await _client.RequestAsync("native.retry");
                    await LoadDiagnosticsAsync();
                }));
                _page.Children.Add(WindowLayout.Divider());
                _page.Children.Add(WindowLayout.Label("退出后将移除收入显示并停止后台程序。再次启动时会按当前时刻重新计算。"));
                _page.Children.Add(Action("退出 WorkFor", ShutdownAsync));
                _ = LoadDiagnosticsAsync();
                break;
            default:
                Heading("收入规则", "收入持续按当前时刻自动计算，每秒刷新。修改规则后先验证，再应用。");
                Add(_rulePicker, _period, _currency, _parameterForm, _parameterFallback, _parametersExpander, _source);
                _page.Children.Add(_manifestExpander);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                buttons.Children.Add(Action("验证规则", ValidateAsync));
                buttons.Children.Add(Action("预览结果", PreviewAsync));
                buttons.Children.Add(Action("验证并应用", ApplyAsync, primary: true));
                _page.Children.Add(buttons);
                _page.Children.Add(_liveSnapshot);
                _page.Children.Add(_preview);
                break;
        }
        SetActionsEnabled();
    }

    private void Heading(string title, string description)
    {
        _page.Children.Add(WindowLayout.Label(title, "TitleTextBlockStyle"));
        var detail = WindowLayout.Label(description);
        detail.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
        _page.Children.Add(detail);
    }

    private void Add(params UIElement[] controls)
    {
        foreach (var control in controls) _page.Children.Add(control);
    }

    private Button Action(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text };
        if (primary) button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        button.Click += async (_, _) => await RunAsync(action);
        _actions.Add(button);
        return button;
    }

    private async Task LoadConfigurationAsync()
    {
        await RunAsync(async () =>
        {
            var config = await _client.RequestAsync("config.get");
            _catalog = RuleCatalogItem.Parse(await _client.RequestAsync("rule.catalog"));
            _config = config;
            PopulateConfiguration();
            _feedback.IsOpen = false;
        });
    }

    private void PopulateConfiguration(string? section = null)
    {
        _populating = true;
        try
        {
            var display = _config?["display"];
            if (section is null or "rules")
            {
                _rulePicker.Items.Clear();
                _rulePicker.Items.Add(new ComboBoxItem { Content = "当前自定义规则" });
                foreach (var rule in _catalog) _rulePicker.Items.Add(new ComboBoxItem { Content = rule.Name, Tag = rule });
                var currentRuleId = _config?["rule"]?["id"]?.GetValue<string>();
                _rulePicker.SelectedIndex = Math.Max(0, _catalog.ToList().FindIndex(rule => rule.Id == currentRuleId) + 1);
                _period.SelectedIndex = display?["period"]?.GetValue<string>() == "month" ? 1 : 0;
                _currency.Text = display?["currency"]?.GetValue<string>() ?? "CNY";
                _parameters.Text = _config?["rule"]?["parameters"]?.ToJsonString(PrettyJson) ?? "{}";
                _source.Text = _config?["rule"]?["source"]?.GetValue<string>() ?? "";
                _manifest.Text = _config?["rule"]?["manifest"]?.ToJsonString(PrettyJson) ?? "";
            }
            if (section is null or "taskbar")
            {
                _width.Value = display?["taskbarWidthDip"]?.GetValue<int>() ?? 260;
                _startWithWindows.IsOn = _config?["startWithWindows"]?.GetValue<bool>() ?? false;
            }
            if (section is null or "calendar")
            {
                _timeZone.Text = _config?["calendar"]?["timeZone"]?.GetValue<string>() ?? "Asia/Shanghai";
                _weekdays.Text = string.Join(",", (_config?["calendar"]?["weekdays"] as JsonArray
                    ?? new JsonArray(1, 2, 3, 4, 5)).Select(value => value?.ToString()));
                _shifts.Text = _config?["calendar"]?["shifts"]?.ToJsonString(PrettyJson) ?? "[]";
                _dateOverrides.Text = _config?["calendar"]?["dateOverrides"]?.ToJsonString(PrettyJson) ?? "{}";
            }
        }
        finally { _populating = false; }
        RefreshParameterForm();
        SetActionsEnabled();
    }

    private void RefreshParameterForm()
    {
        try
        {
            var parameters = JsonNode.Parse(_parameters.Text,
                documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                ?? throw new InvalidDataException("规则参数必须是 JSON 对象。");
            var manifest = JsonNode.Parse(_manifest.Text,
                documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject;
            var schema = manifest?["parametersSchema"];
            var key = schema?.ToJsonString() ?? "";
            if (_parameterSchemaKey == key && _parameterForm.Visibility == Visibility.Visible
                && _parameterForm.UpdateParameters(parameters, out _)) return;
            var native = _parameterForm.Configure(schema, parameters, out var reason);
            _parameterSchemaKey = key;
            _parameterForm.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
            _parameterFallback.Text = reason;
            _parameterFallback.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
            if (!native) _parametersExpander.IsExpanded = true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
        {
            _parameterForm.Visibility = Visibility.Collapsed;
            _parameterFallback.Visibility = Visibility.Visible;
            _parameterFallback.Text = "参数或清单 JSON 尚未完整，请在高级编辑中修正后验证。";
            _parametersExpander.IsExpanded = true;
        }
    }

    private JsonObject Candidate()
    {
        if (_config is null) throw new InvalidOperationException("尚未读取配置。请先启动计算宿主。");
        if (_parameterForm.Visibility == Visibility.Visible) _parameterForm.CommitPendingEdits();
        var calendar = _config["calendar"]!.AsObject();
        var weekdays = string.Join(",", calendar["weekdays"]!.AsArray().Select(day => day!.ToString()));
        return new SettingsDraft(
            (_period.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "day", _currency.Text,
            _config["display"]!["taskbarWidthDip"]!.GetValue<int>(), _config["startWithWindows"]!.GetValue<bool>(),
            _parameters.Text, _source.Text, _manifest.Text, calendar["timeZone"]!.GetValue<string>(), weekdays,
            calendar["shifts"]!.ToJsonString(), calendar["dateOverrides"]?.ToJsonString() ?? "{}").BuildCandidate(_config);
    }

    private async Task ValidateAsync()
    {
        await _client.RequestAsync("rule.validate", Candidate());
        Feedback("验证通过", "当前修改可应用，现有计算规则尚未改变。", InfoBarSeverity.Success);
    }

    private async Task PreviewAsync()
    {
        var result = await _client.RequestAsync("rule.preview", Candidate());
        _preview.Text = result.ToJsonString(PrettyJson);
        Feedback("预览完成", "此结果未改变当前活动规则。", InfoBarSeverity.Success);
    }

    private async Task ApplyAsync()
    {
        if (_config is null) throw new InvalidOperationException("尚未读取配置。");
        var section = _section;
        var candidate = section switch
        {
            "calendar" => new JsonObject
            {
                ["expectedRevision"] = _config["revision"]!.DeepClone(),
                ["calendar"] = SettingsDraft.BuildCalendar(_config["calendar"] as JsonObject,
                    _timeZone.Text, _weekdays.Text, _shifts.Text, _dateOverrides.Text)
            },
            "taskbar" => new JsonObject
            {
                ["expectedRevision"] = _config["revision"]!.DeepClone(),
                ["display"] = new JsonObject { ["taskbarWidthDip"] = checked((int)_width.Value) },
                ["startWithWindows"] = _startWithWindows.IsOn
            },
            _ => Candidate()
        };
        await _client.RequestAsync("rule.validate", candidate.DeepClone() as JsonObject);
        await _client.RequestAsync("config.activate", candidate);
        _config = await _client.RequestAsync("config.get");
        PopulateConfiguration(section);
        Feedback("已应用", "计算宿主将按新规则立即更新金额。", InfoBarSeverity.Success);
    }

    private async Task ShutdownAsync()
    {
        await _client.RequestAsync("host.shutdown");
        App.CloseAllWindows();
    }

    private async Task LoadDiagnosticsAsync()
    {
        try
        {
            var result = await _client.RequestAsync("diagnostics.get");
            _diagnostics.Text = result.ToJsonString(PrettyJson);
            if (result["configRecoveryNotice"] is JsonValue notice
                && notice.TryGetValue<string>(out var recovery) && !string.IsNullOrWhiteSpace(recovery))
                Feedback("已恢复设置", recovery, InfoBarSeverity.Warning);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or OperationCanceledException or TimeoutException
            or HostRequestException or InvalidOperationException)
        {
            _diagnostics.Text = $"无法读取诊断信息。{error.Message}";
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetActionsEnabled();
        try { await action(); }
        catch (HostRequestException error)
        {
            Feedback(error.Code == "CONFIG_CONFLICT" ? "配置已在其他窗口更新" : "修改未生效",
                UiMessages.Error(error.Code, error.Message), InfoBarSeverity.Error);
            if (error.Code == "CONFIG_CONFLICT")
                _connection.Text = "请关闭并重新打开设置，读取最新配置后再应用。";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or OperationCanceledException or TimeoutException
            or JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            Feedback("操作未完成", error is OperationCanceledException or TimeoutException
                ? "计算宿主未及时响应，请确认后台程序正在运行。" : error.Message, InfoBarSeverity.Error);
        }
        finally { _busy = false; SetActionsEnabled(); }
    }

    private void SetActionsEnabled()
    {
        _navigation.IsEnabled = !_busy;
        foreach (var action in _actions) action.IsEnabled = !_busy && _config is not null;
        _rulePicker.IsEnabled = !_busy && _config is not null;
        _exitItem.IsEnabled = !_busy;
    }

    private void Feedback(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        _feedback.Title = title; _feedback.Message = message; _feedback.Severity = severity; _feedback.IsOpen = true;
    }

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
}
