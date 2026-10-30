using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WorkFor.Contracts;

namespace WorkFor.Host;

internal sealed class PipeHub(Func<string, JsonObject, CancellationToken, Task<JsonObject>> control,
    Action<JsonObject> openDetails) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Channel<JsonObject>> _subscribers = [];
    private readonly List<Task> _clients = [];
    private JsonObject? _latest;
    private Task[] _listeners = [];
    private readonly SemaphoreSlim _displayConnections = new(12);
    private readonly SemaphoreSlim _controlConnections = new(12);

    public static string Name(string channel) => $"workfor-{channel}-v1-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";

    public void Start() => _listeners = [ListenAsync("display"), ListenAsync("control")];

    public void Publish(JsonObject snapshot)
    {
        lock (_gate)
        {
            _latest = snapshot;
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(snapshot);
        }
    }

    private async Task ListenAsync(string channel)
    {
        var slots = channel == "display" ? _displayConnections : _controlConnections;
        while (!_stop.IsCancellationRequested)
        {
            await slots.WaitAsync(_stop.Token);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Name(channel), PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                lock (_gate)
                {
                    _clients.RemoveAll(x => x.IsCompleted);
                    _clients.Add(ServeLimitedAsync(pipe, channel, slots));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { pipe?.Dispose(); slots.Release(); return; }
            catch (IOException)
            {
                pipe?.Dispose(); slots.Release();
                await Task.Delay(250, _stop.Token);
            }
        }
    }

    private async Task ServeLimitedAsync(NamedPipeServerStream pipe, string channel, SemaphoreSlim slots)
    {
        try { await ServeAsync(pipe, channel); }
        finally { slots.Release(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, string channel)
    {
        await using (pipe)
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                var limit = channel == "display" ? FramedJson.DisplayLimit : FramedJson.ControlLimit;
                var message = await FramedJson.ReadAsync(pipe, limit, handshake.Token);
                if (message["protocolVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported protocol.");
                var type = message["type"]?.GetValue<string>() ?? "";
                var requestId = message["requestId"]?.GetValue<string>() ?? "";
                if (requestId.Length > 80) throw new InvalidDataException("Request id too long.");
                var payload = message["payload"] as JsonObject ?? new JsonObject();
                if (channel == "display" && type == "display.subscribe")
                {
                    var updates = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1)
                    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
                    lock (_gate)
                    {
                        if (_subscribers.Count >= 12) throw new InvalidDataException("Too many subscribers.");
                        _subscribers.Add(updates);
                        if (_latest is not null) updates.Writer.TryWrite(_latest);
                    }
                    try
                    {
                        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        var receive = ReceiveDisplayEventsAsync(pipe, connection.Token);
                        var send = SendSnapshotsAsync(pipe, updates.Reader, requestId, connection.Token);
                        await Task.WhenAny(receive, send);
                        await connection.CancelAsync();
                        pipe.Dispose();
                        try { await Task.WhenAll(receive, send); }
                        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
                    }
                    finally { lock (_gate) _subscribers.Remove(updates); updates.Writer.TryComplete(); }
                }
                else if (channel == "control")
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    JsonObject response;
                    try { response = await control(type, payload, requestDeadline.Token); }
                    catch (ConfigException error)
                    {
                        response = Error(error.Code, error.Message);
                        if (error.Line is { } line) response["line"] = line;
                        if (error.Column is { } column) response["column"] = column;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
                    { response = Error("REQUEST_INVALID", "参数或配置不合法：" + error.Message); }
                    var responseType = response.ContainsKey("code") ? "control.error" : "control.result";
                    await FramedJson.WriteAsync(pipe, FramedJson.Message(responseType, requestId, response), limit, requestDeadline.Token);
                }
                else throw new InvalidDataException("Invalid message type for channel.");
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException
                or System.Text.Json.JsonException or ObjectDisposedException or InvalidOperationException)
            { /* A malformed or disconnected client cannot stop the host. */ }
        }
    }

    private async Task ReceiveDisplayEventsAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await FramedJson.ReadAsync(pipe, FramedJson.DisplayLimit, cancellationToken);
            if (message["protocolVersion"]?.GetValue<int>() == 1 && message["type"]?.GetValue<string>() == "ui.openDetails")
                openDetails(message["payload"] as JsonObject ?? new());
        }
    }

    private static async Task SendSnapshotsAsync(Stream pipe, ChannelReader<JsonObject> updates, string requestId, CancellationToken cancellationToken)
    {
        await foreach (var snapshot in updates.ReadAllAsync(cancellationToken))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await FramedJson.WriteAsync(pipe, FramedJson.Message("display.snapshot", requestId, snapshot.DeepClone()), FramedJson.DisplayLimit, deadline.Token);
        }
    }

    public static JsonObject Error(string code, string message) => new() { ["code"] = code, ["message"] = message };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await Task.WhenAll(_listeners); } catch (OperationCanceledException) { }
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        await Task.WhenAll(clients);
        _displayConnections.Dispose();
        _controlConnections.Dispose();
        _stop.Dispose();
    }
}
