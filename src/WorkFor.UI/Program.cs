using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WorkFor.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var pipeName = HostClient.PipeName("ui");
        using var ownership = new Mutex(true, @"Local\" + pipeName, out var primary);
        if (!primary)
        {
            try
            {
                Task.Run(async () =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(deadline.Token);
                    if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid))
                        AllowSetForegroundWindow(serverPid);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(args);
                    var header = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
                    await pipe.WriteAsync(header, deadline.Token);
                    await pipe.WriteAsync(bytes, deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                }).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
            {
                return 2;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            var app = new App();
            app.StartActivationListener(pipeName);
        });
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
