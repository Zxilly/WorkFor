using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using WorkFor.UI;

namespace WorkFor.FlyoutProbe;

// Calls the real product window code, activating only windows owned by this probe.
public partial class App : Application
{
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var start = new Button { Content = "验证浮窗", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        start.Click += (_, _) => clicked.TrySetResult();
        var companion = new Window { Title = "WorkFor owned flyout probe", Content = start };
        DetailsWindow? details = null;
        var closed = false;
        var checks = new Dictionary<string, bool>();
        string? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prefix = "workfor-flyout-probe-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(prefix + "-display", PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var sending = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var header = new byte[4];
            await server.ReadExactlyAsync(header, deadline.Token);
            var body = new byte[BitConverter.ToInt32(header)];
            await server.ReadExactlyAsync(body, deadline.Token);
            await SendSnapshot(server, "¥123.12", 1, deadline.Token);
        });
        await using var client = new HostClient(channel => prefix + "-" + channel);
        try
        {
            await sending;
            while (client.LatestSnapshot is null) await Task.Delay(10, deadline.Token);
            WindowLayout.Activate(companion);
            if (Environment.GetCommandLineArgs().Contains("--interactive")) await clicked.Task.WaitAsync(deadline.Token);
            await Task.Delay(150);
            var area = DisplayArea.Primary.WorkArea;
            var anchor = new Windows.Graphics.RectInt32(area.X + area.Width / 2 - 54, area.Y + area.Height - 48, 108, 48);
            details = new DetailsWindow(anchor, client);
            var body = ((ScrollViewer)details.Content).Content as StackPanel;
            var amount = body!.Children.OfType<Grid>().First().Children.OfType<TextBlock>().First();
            checks["value-before-first-frame"] = amount.Text == "¥123.12";
            details.Closed += (_, _) => closed = true;
            WindowLayout.Activate(details);
            await Task.Delay(150);
            checks["topmost"] = details.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(details);
            checks["native-topmost"] = (GetWindowLongPtrW(hwnd, -20).ToInt64() & 8) != 0;
            checks["foreground-on-open"] = GetForegroundWindow() == hwnd;
            checks["centered-above-anchor"] = Math.Abs(details.AppWindow.Position.X + details.AppWindow.Size.Width / 2.0 - (anchor.X + anchor.Width / 2.0)) <= 1;
            checks["hidden-from-switchers"] = !details.AppWindow.IsShownInSwitchers;
            WindowLayout.Activate(companion);
            await Task.Delay(150);
            checks["dismiss-on-deactivate"] = !details.AppWindow.IsVisible;
            checks["kept-ready-after-dismiss"] = !closed;
            await SendSnapshot(server, "¥456.78", 2, deadline.Token);
            while (client.LatestSnapshot?.AmountText != "¥456.78") await Task.Delay(10, deadline.Token);
            details.MoveToAnchor(anchor);
            checks["latest-value-before-reopen"] = amount.Text == "¥456.78";
            WindowLayout.Activate(details);
            await Task.Delay(100);
            checks["same-window-reopened"] = details.AppWindow.IsVisible && !closed;
            checks["settings-stays-normal"] = companion.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false };
            if (checks.Values.Any(passed => !passed)) failure = "The real flyout window failed a behavior check.";
        }
        catch (Exception error) { failure = error.ToString(); }
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--result");
        var output = index >= 0 ? arguments[index + 1] : Path.Combine(Environment.CurrentDirectory, ".local", "flyout-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failure is null, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
        if (!closed) details?.Close();
        companion.Close();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static async Task SendSnapshot(Stream stream, string amount, int sequence, CancellationToken token)
    {
        var payload = new JsonObject
        {
            ["protocolVersion"] = 1, ["type"] = "display.snapshot", ["requestId"] = "probe",
            ["payload"] = new JsonObject
            {
                ["generation"] = "probe", ["sequence"] = sequence, ["title"] = "今日收入",
                ["amountText"] = amount, ["status"] = "fresh", ["statusText"] = "实时更新", ["currency"] = "CNY"
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
}
