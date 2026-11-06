using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.UI.ViewManagement;

namespace WorkFor.UI;

/// <summary>Native editor chrome, offline Monaco, and a local stdio language server.</summary>
internal sealed class CodeEditor : UserControl, IDisposable
{
    private const string Origin = "https://workfor-editor.invalid";
    private static Task<CoreWebView2Environment>? _environment;
    private readonly WebView2 _view = new();
    private readonly TextBox _fallback = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), Visibility = Visibility.Collapsed };
    private readonly TextBlock _header = WindowLayout.Label("");
    private readonly TextBlock _description = WindowLayout.Label("", "CaptionTextBlockStyle");
    private readonly TextBlock _status = WindowLayout.Label("", "CaptionTextBlockStyle");
    private readonly Button _format = new() { Content = "格式化", IsEnabled = false };
    private readonly Button _retry = new() { Content = "重试", Visibility = Visibility.Collapsed };
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private readonly double _minimumHeight;
    private LanguageServerSession? _language;
    private Task? _initializing;
    private bool _ready, _languageReady, _coreConfigured, _disposed, _updatingFallback, _readOnly;
    private TaskCompletionSource? _formatCompletion;
    private long _formatId;
    private string _text = "";
    private long _revision;
    private JsonNode? _schema;
    public string Kind { get; }
    public event Action? TextChanged;
    public string Header { get => _header.Text; set { _header.Text = value; Push(); } }
    public string Description { get => _description.Text; set { _description.Text = value; _description.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible; } }
    public string PlaceholderText { get => _fallback.PlaceholderText; set => _fallback.PlaceholderText = value; }
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value; _revision++;
            _updatingFallback = true;
            try { _fallback.Text = value; } finally { _updatingFallback = false; }
            Push(); TextChanged?.Invoke();
        }
    }
    public JsonNode? ParametersSchema { set { _schema = value?.DeepClone(); _language?.UpdateSchema(_schema); Push(); } }
    public bool EditorReadOnly { set { _readOnly = value; _fallback.IsReadOnly = value; _format.IsEnabled = _languageReady && !value; Push(); } }

    public CodeEditor(string header, string kind, double height)
    {
        Kind = kind; Header = header; _minimumHeight = height;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.Height = height; _view.MinWidth = 100;
        _fallback.MinHeight = height; _fallback.MaxHeight = 420;
        var body = new StackPanel { Spacing = 8 };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_header); Grid.SetColumn(_format, 1); heading.Children.Add(_format);
        body.Children.Add(heading);
        var surface = new Grid(); surface.Children.Add(_view); surface.Children.Add(_fallback);
        body.Children.Add(new Border { BorderBrush = WindowLayout.ResourceBrush("ControlStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = surface });
        body.Children.Add(_description);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        status.Children.Add(_status); status.Children.Add(_retry); body.Children.Add(status);
        Content = body; Description = "";
        _fallback.TextChanged += (_, _) => { if (!_updatingFallback) { _text = _fallback.Text; _revision++; TextChanged?.Invoke(); } };
        _format.Click += async (_, _) =>
        {
            try { await FormatAsync(); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException) { _status.Text = "格式化暂时不可用，请稍后重试。"; }
        };
        _retry.Click += async (_, _) =>
        {
            try { await FlushAsync(); } catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            _ready = false; _languageReady = false; _revision++; _language?.Dispose(); _language = null;
            _initializing = null; await EnsureAsync();
        };
        Loaded += async (_, _) => await EnsureAsync();
        ActualThemeChanged += (_, _) => Push();
        _uiSettings.ColorValuesChanged += OnColorsChanged;
    }

    public Task EnsureAsync() => _initializing ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_disposed) return;
        try
        {
            _status.Text = "正在准备代码编辑器";
            var folder = Path.Combine(AppContext.BaseDirectory, "editor");
            if (!File.Exists(Path.Combine(folder, "index.html"))) throw new FileNotFoundException("Offline editor assets are missing.");
            if (_environment is { IsFaulted: true } or { IsCanceled: true }) _environment = null;
            _environment ??= CreateEnvironmentAsync();
            await _view.EnsureCoreWebView2Async(await _environment);
            if (_disposed) return;
            var core = _view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("workfor-editor.invalid", folder, CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            if (!_coreConfigured)
            {
                core.NavigationStarting += (_, args) => { if (args.Uri != Origin + "/index.html") args.Cancel = true; };
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnMessage;
                _coreConfigured = true;
            }
            _view.Visibility = Visibility.Visible; _fallback.Visibility = Visibility.Collapsed;
            core.Navigate(Origin + "/index.html");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { Fail("代码编辑器暂时不可用，可继续编辑纯文本。"); }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() => await CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkFor", "WebView2"), null);

    private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || args.Source != Origin + "/index.html") return;
        try
        {
            if (args.WebMessageAsJson.Length > 3 * 1024 * 1024) return;
            var message = JsonNode.Parse(args.WebMessageAsJson)!.AsObject();
            switch (message["type"]?.GetValue<string>())
            {
                case "ready":
                    _language?.Dispose();
                    _language = new LanguageServerSession(Path.Combine(AppContext.BaseDirectory, "editor"), Kind == "script", _schema,
                        response => DispatcherQueue.TryEnqueue(() => { if (!_disposed) _view.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "lsp", ["message"] = response }.ToJsonString()); }),
                        error => DispatcherQueue.TryEnqueue(() => Fail(error)));
                    _ready = true; _status.Text = "正在连接代码提示"; Push(); break;
                case "language-ready": _languageReady = true; _status.Text = "代码提示已就绪"; _format.IsEnabled = !_readOnly; _retry.Visibility = Visibility.Collapsed; break;
                case "formatted":
                    if (message["id"]?.GetValue<long>() == _formatId)
                    {
                        if (message["error"] is JsonValue error) _formatCompletion?.TrySetException(new InvalidOperationException(error.ToString()));
                        else _formatCompletion?.TrySetResult();
                    }
                    break;
                case "lsp": if (_language is not null && message["message"] is JsonObject rpc) await _language.SendAsync(rpc); break;
                case "changed":
                    if (message["revision"]?.GetValue<long>() == _revision) AcceptText(message["value"]!.GetValue<string>());
                    break;
                case "height": _view.Height = Math.Clamp(message["height"]!.GetValue<double>(), _minimumHeight, 420); break;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { if (!_disposed) Fail("代码提示暂时不可用，可继续编辑和验证。"); }
    }

    private void AcceptText(string value)
    {
        if (value.Length > 256 * 1024 || value == _text) return;
        _text = value;
        _updatingFallback = true; try { _fallback.Text = value; } finally { _updatingFallback = false; }
        TextChanged?.Invoke();
    }

    public async Task FlushAsync()
    {
        if (!_ready || _disposed) return;
        var state = JsonNode.Parse(await _view.ExecuteScriptAsync("window.workforEditor.getState()"));
        if (state?["revision"]?.GetValue<long>() == _revision) AcceptText(state["value"]!.GetValue<string>());
    }

    public async Task FormatAsync()
    {
        if (!_languageReady || _disposed || _readOnly) return;
        _formatCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); _format.IsEnabled = false;
        try
        {
            _view.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "format", ["id"] = ++_formatId }.ToJsonString());
            await _formatCompletion.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await FlushAsync();
        }
        finally { _format.IsEnabled = _languageReady && !_readOnly && !_disposed; }
    }

    private void Push()
    {
        if (!_ready || _disposed || _language is null) return;
        var dark = ActualTheme == ElementTheme.Dark;
        string Color(string key, string fallback) => Application.Current.Resources[key] is SolidColorBrush brush
            ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}" : fallback;
        _view.CoreWebView2.PostWebMessageAsJson(new JsonObject
        {
            ["revision"] = _revision, ["value"] = _text, ["kind"] = Kind, ["label"] = Header, ["readOnly"] = _readOnly,
            ["parametersSchema"] = _schema?.DeepClone(), ["documentUri"] = _language.DocumentUri,
            ["workspaceUri"] = _language.WorkspaceUri, ["tsserverPath"] = _language.TypeScriptServerPath,
            ["theme"] = new JsonObject { ["name"] = _accessibility.HighContrast ? dark ? "hc-black" : "hc-light" : dark ? "vs-dark" : "vs",
                ["background"] = Color("SolidBackgroundFillColorBaseBrush", dark ? "#202020" : "#FFFFFF"), ["foreground"] = Color("TextFillColorPrimaryBrush", dark ? "#FFFFFF" : "#000000") }
        }.ToJsonString());
    }

    private void Fail(string message)
    {
        if (_disposed) return;
        _languageReady = false; _format.IsEnabled = false;
        _status.Text = message; _retry.Visibility = Visibility.Visible;
        if (!_ready) { _view.Visibility = Visibility.Collapsed; _fallback.Visibility = Visibility.Visible; }
    }
    private void OnColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Push);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _uiSettings.ColorValuesChanged -= OnColorsChanged;
        _language?.Dispose(); _view.Close();
    }
}
