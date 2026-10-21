using System.Text.Json;

namespace AppleMusicPresence;

public sealed record PresenceSnapshot(MediaState Media, string Status, string Hint, bool Live, string? ArtworkUrl = null, ActivityTimestamps? Timestamps = null);

public sealed class PresenceService(Func<AppSettings> getSettings, Action<PresenceSnapshot> publish,
    Func<IMediaReader>? mediaFactory = null, Func<IArtworkResolver>? artworkFactory = null,
    Func<IDiscordRpc>? rpcFactory = null, TimeSpan? pollInterval = null, Func<DateTimeOffset>? utcNow = null)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var reader = mediaFactory?.Invoke() ?? new MediaWorker();
        var clock = new PlaybackClock();
        using var artwork = artworkFactory?.Invoke() ?? new ArtworkResolver();
        await using var rpc = rpcFactory?.Invoke() ?? new DiscordRpc();
        CancellationTokenSource? artStop = null;
        Task<Artwork?>? artTask = null;
        string? artKey = null;
        string? sentPayload = null;
        bool hasPresence = false;
        DateTimeOffset nextConnect = DateTimeOffset.MinValue;
        DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
        string? connectedId = null;
        var media = new MediaState(null, false);
        string connectionError = "Waiting for Discord desktop.";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var settings = getSettings();
                string? mediaError = null;
                try
                {
                    using var readStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readStop.CancelAfter(TimeSpan.FromSeconds(5));
                    media = await reader.ReadAsync(readStop.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    reader.Reset();
                    media = new(null, false);
                    mediaError = "Cannot read Apple Music. Retrying…";
                }

                var lookupKey = settings.Enabled && media.Playing && media.Track is not null ? settings.SafeCountry + "\0" + media.Track.Key : null;
                if (lookupKey != artKey)
                {
                    artStop?.Cancel();
                    artStop?.Dispose();
                    artStop = null;
                    artTask = null;
                    artKey = lookupKey;
                    if (lookupKey is not null)
                    {
                        artStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        artTask = artwork.ResolveAsync(media.Track!, settings.SafeCountry, artStop.Token);
                    }
                }
                if (artTask is { IsCompletedSuccessfully: true } && artTask.Result is null)
                {
                    // A failed lookup is cached briefly; retry without keeping stale artwork.
                    artTask = artwork.ResolveAsync(media.Track!, settings.SafeCountry, artStop!.Token);
                }
                var artUrl = artTask is { IsCompletedSuccessfully: true } ? artTask.Result?.Url : null;

                if (connectedId != settings.ApplicationId)
                {
                    await ClearAsync(rpc, hasPresence);
                    await rpc.DisconnectAsync();
                    connectedId = settings.ApplicationId;
                    hasPresence = false;
                    sentPayload = null;
                    nextConnect = DateTimeOffset.MinValue;
                }
                var valid = AppSettings.IsValidId(settings.ApplicationId);
                bool wantPresence = settings.Enabled && valid && media.Playing && media.Track is not null;
                var timestamps = clock.Observe(media, utcNow?.Invoke() ?? DateTimeOffset.UtcNow);
                if (wantPresence && !rpc.Connected && DateTimeOffset.UtcNow >= nextConnect)
                {
                    try
                    {
                        await rpc.ConnectAsync(settings.ApplicationId, ct);
                        sentPayload = null;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception e)
                    {
                        connectionError = e is RpcException ? e.Message : "Discord connection interrupted. Retrying…";
                        nextConnect = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                }
                if (wantPresence && rpc.Connected)
                {
                    var activity = PresencePayload.Create(media.Track!, artUrl, timestamps);
                    var fingerprint = JsonSerializer.Serialize(activity);
                    if (fingerprint != sentPayload || DateTimeOffset.UtcNow >= nextRefresh)
                    {
                        try
                        {
                            await rpc.SetActivityAsync(activity, ct);
                            sentPayload = fingerprint;
                            hasPresence = true;
                            nextRefresh = DateTimeOffset.UtcNow.AddSeconds(20);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception e)
                        {
                            connectionError = e is RpcException ? e.Message : "Discord connection interrupted. Retrying…";
                            await rpc.DisconnectAsync();
                            hasPresence = false;
                            sentPayload = null;
                            nextConnect = DateTimeOffset.UtcNow.AddSeconds(10);
                        }
                    }
                }
                else if (!wantPresence && rpc.Connected)
                {
                    await ClearAsync(rpc, hasPresence);
                    await rpc.DisconnectAsync();
                    hasPresence = false;
                    sentPayload = null;
                }
                bool live = wantPresence && rpc.Connected && hasPresence;
                var (status, hint) = !settings.Enabled ? ("Presence is off", "Turn sharing on to show your music.")
                    : !valid ? ("One-time setup", "Add your Discord Application ID above.")
                    : mediaError is not null ? ("Waiting for Apple Music", mediaError)
                    : media.Track is null ? ("Waiting for Apple Music", "Play a song in the Apple Music Windows app.")
                    : !media.Playing ? ("Playback paused", "Your Discord presence is hidden.")
                    : !live ? ("Connecting to Discord", connectionError)
                    : ("Live on Discord", artUrl is not null ? "Sharing song, artist, and cover." : "Sharing song and artist. Looking for a matching cover.");
                publish(new(media, status, hint, live, artUrl, timestamps));
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            artStop?.Cancel();
            if (artTask is not null) { try { await artTask; } catch (OperationCanceledException) { } }
            artStop?.Dispose();
            await ClearAsync(rpc, hasPresence);
            if (reader is IAsyncDisposable disposableReader) await disposableReader.DisposeAsync();
        }
    }

    private static async Task ClearAsync(IDiscordRpc rpc, bool hasPresence)
    {
        if (!hasPresence || !rpc.Connected) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await rpc.SetActivityAsync(null, stop.Token); }
        catch { /* Closing the IPC connection also releases this process's presence. */ }
    }
}
