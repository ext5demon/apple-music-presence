using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace AppleMusicPresence;

public sealed class RpcException(string message) : IOException(message);

public static class PresencePayload
{
    public const string DisplayName = "Apple Music";

    public static Dictionary<string, object> Create(Track track, string? artworkUrl, ActivityTimestamps? timestamps = null)
    {
        // Title, artist, cover, and the playback clock. No album text or buttons.
        var activity = new Dictionary<string, object>
        {
            ["name"] = DisplayName,
            ["type"] = 2,
            ["status_display_type"] = 0,
            ["details"] = Limit(track.Title)
        };
        if (!string.IsNullOrWhiteSpace(track.Artist)) activity["state"] = Limit(track.Artist);
        if (!string.IsNullOrWhiteSpace(artworkUrl)) activity["assets"] = new { large_image = artworkUrl };
        if (timestamps is not null) activity["timestamps"] = new { start = timestamps.Start, end = timestamps.End };
        return activity;
    }
    public static string Limit(string text)
    {
        text = text.Trim();
        if (text.Length <= 128) return text;
        var length = char.IsHighSurrogate(text[127]) ? 127 : 128;
        return text[..length];
    }
}

public interface IDiscordRpc : IAsyncDisposable
{
    bool Connected { get; }
    Task ConnectAsync(string applicationId, CancellationToken ct);
    Task SetActivityAsync(object? activity, CancellationToken ct);
    Task DisconnectAsync();
}

public sealed class DiscordRpc : IDiscordRpc
{
    private sealed class Connection(NamedPipeClientStream pipe)
    {
        public readonly NamedPipeClientStream Pipe = pipe;
        public readonly SemaphoreSlim WriteGate = new(1, 1);
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentDictionary<string, TaskCompletionSource> Pending = new();
        public Task? Reader;
    }
    private readonly string[] pipeNames;
    private Connection? connection;
    private string? clientId;
    public bool Connected => connection is { } c && c.Ready.Task.IsCompletedSuccessfully && !c.Stop.IsCancellationRequested;
    public DiscordRpc(string[]? pipeNames = null) => this.pipeNames = pipeNames ?? Enumerable.Range(0, 10).Select(i => $"discord-ipc-{i}").ToArray();

    public async Task ConnectAsync(string applicationId, CancellationToken ct)
    {
        if (applicationId == clientId && Connected) return;
        await DisconnectAsync();
        foreach (var name in pipeNames)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(160));
                await pipe.ConnectAsync(timeout.Token);
            }
            catch (Exception e) when (e is TimeoutException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                pipe.Dispose();
                continue;
            }
            catch { pipe.Dispose(); throw; }
            var c = new Connection(pipe);
            connection = c;
            clientId = applicationId;
            try
            {
                await WriteAsync(c, 0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = applicationId }), ct);
                c.Reader = ReadLoopAsync(c);
                await c.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                return;
            }
            catch { await DisconnectAsync(); throw; }
        }
        throw new RpcException("Waiting for Discord desktop. Open Discord and sign in.");
    }

    public async Task SetActivityAsync(object? activity, CancellationToken ct)
    {
        var c = connection;
        if (c is null || !Connected) throw new RpcException("Discord disconnected. Reconnecting…");
        var nonce = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Pending[nonce] = completion;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce });
            await WriteAsync(c, 1, payload, ct);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally { c.Pending.TryRemove(nonce, out _); }
    }

    private static async Task WriteAsync(Connection c, int opcode, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        await c.WriteGate.WaitAsync(ct);
        try
        {
            await c.Pipe.WriteAsync(frame, ct);
            await c.Pipe.FlushAsync(ct);
        }
        finally { c.WriteGate.Release(); }
    }

    private static async Task ReadLoopAsync(Connection c)
    {
        Exception failure = new RpcException("Discord disconnected. Reconnecting…");
        try
        {
            var header = new byte[8];
            while (!c.Stop.IsCancellationRequested)
            {
                await c.Pipe.ReadExactlyAsync(header, c.Stop.Token);
                var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (length is < 0 or > 1_048_576) throw new RpcException("Discord sent an invalid IPC frame.");
                var body = new byte[length];
                await c.Pipe.ReadExactlyAsync(body, c.Stop.Token);
                if (opcode == 3) { await WriteAsync(c, 4, body, c.Stop.Token); continue; }
                if (opcode == 4) continue;
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (opcode == 2) throw new RpcException(Message(root));
                var evt = root.TryGetProperty("evt", out var ev) ? ev.GetString() : null;
                if (evt == "READY") { c.Ready.TrySetResult(); continue; }
                var nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (nonce is not null && c.Pending.TryRemove(nonce, out var ack))
                {
                    if (evt == "ERROR") ack.TrySetException(new RpcException(Message(root)));
                    else ack.TrySetResult();
                }
                else if (evt == "ERROR") throw new RpcException(Message(root));
            }
        }
        catch (Exception e) { failure = e; }
        finally
        {
            c.Stop.Cancel();
            c.Pipe.Dispose();
            c.Ready.TrySetException(failure);
            foreach (var pending in c.Pending.Values) pending.TrySetException(failure);
            c.Pending.Clear();
        }
    }

    private static string Message(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var message))
            return "Discord: " + message.GetString();
        if (root.TryGetProperty("message", out var direct)) return "Discord: " + direct.GetString();
        return "Discord rejected the connection. Check the Application ID.";
    }

    public async Task DisconnectAsync()
    {
        var old = connection;
        connection = null;
        clientId = null;
        if (old is null) return;
        old.Stop.Cancel();
        old.Pipe.Dispose();
        if (old.Reader is not null) await old.Reader;
        old.Stop.Dispose();
        old.WriteGate.Dispose();
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
