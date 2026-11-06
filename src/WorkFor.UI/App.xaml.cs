using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace WorkFor.UI;

public partial class App : Application
{
    private static readonly HashSet<Window> Windows = [];
    private readonly CancellationTokenSource _activationStop = new();
    private readonly DispatcherQueue _queue;
    private int _watchedHost;

    public App()
    {
        InitializeComponent();
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        ActivateArguments(commandLine);
    }

    internal void StartActivationListener(string pipeName) => _ = Task.Run(async () =>
    {
        while (!_activationStop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_activationStop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_activationStop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, deadline.Token);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length is 0 or > 4096) continue;
                var bytes = new byte[(int)length];
                await server.ReadExactlyAsync(bytes, deadline.Token);
                var args = JsonSerializer.Deserialize<string[]>(bytes);
                if (args is not null) _queue.TryEnqueue(() => ActivateArguments(args));
            }
            catch (OperationCanceledException) when (_activationStop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        }
    });

    private static void ActivateArguments(string[] args)
    {
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase)) { CloseAllWindows(); return; }
        if (args.Contains("--warm", StringComparer.OrdinalIgnoreCase))
        {
            if (Current is App app) app.WatchHost(args);
            if (!Windows.OfType<DetailsWindow>().Any()) Track(new DetailsWindow());
            return;
        }
        var settings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var existing = Windows.FirstOrDefault(window => settings ? window is SettingsWindow : window is DetailsWindow);
        if (existing is not null)
        {
            if (existing is DetailsWindow details) details.MoveToAnchor(WindowLayout.ParseAnchor(args));
            WindowLayout.Activate(existing);
            return;
        }
        Show(settings ? new SettingsWindow() : new DetailsWindow(WindowLayout.ParseAnchor(args)));
    }

    internal static void Show(Window window)
    {
        Track(window);
        WindowLayout.Activate(window);
    }

    private static void Track(Window window)
    {
        Windows.Add(window);
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            if (Windows.Count == 0 && Current is App app) app._activationStop.Cancel();
        };
    }

    internal static void CloseAllWindows()
    {
        foreach (var window in Windows.ToArray()) window.Close();
    }

    internal static void OpenSettings() => ActivateArguments(["--settings"]);

    private void WatchHost(string[] args)
    {
        var index = Array.IndexOf(args, "--host-pid");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid == _watchedHost) return;
        _watchedHost = pid;
        _ = Task.Run(async () =>
        {
            try
            {
                using var host = System.Diagnostics.Process.GetProcessById(pid);
                if (host.SessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId || host.ProcessName != "WorkFor.Host") return;
                await host.WaitForExitAsync(_activationStop.Token);
                _queue.TryEnqueue(() => { if (_watchedHost == pid) CloseAllWindows(); });
            }
            catch (ArgumentException) { _queue.TryEnqueue(() => { if (_watchedHost == pid) CloseAllWindows(); }); }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
    }
}
