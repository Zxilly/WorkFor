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
        var settings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var existing = Windows.FirstOrDefault(window => settings ? window is SettingsWindow : window is DetailsWindow);
        if (existing is not null)
        {
            if (existing is DetailsWindow details) details.MoveToAnchor(WindowLayout.ParseAnchor(args));
            existing.Activate();
            return;
        }
        Show(settings ? new SettingsWindow() : new DetailsWindow(WindowLayout.ParseAnchor(args)));
    }

    internal static void Show(Window window)
    {
        Windows.Add(window);
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            if (Windows.Count == 0 && Current is App app) app._activationStop.Cancel();
        };
        window.Activate();
    }

    internal static void CloseAllWindows()
    {
        foreach (var window in Windows.ToArray()) window.Close();
    }

    internal static void OpenSettings() => ActivateArguments(["--settings"]);
}
