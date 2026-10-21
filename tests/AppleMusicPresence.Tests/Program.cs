using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using AppleMusicPresence;

int passed = 0;
await Check("Apple Music filtering excludes Spotify and browsers", () =>
{
    Assert(MediaReader.IsAppleMusic("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));
    Assert(MediaReader.IsAppleMusic("AppleMusic.exe"));
    Assert(!MediaReader.IsAppleMusic("Spotify.exe"));
    Assert(!MediaReader.IsAppleMusic("chrome.exe"));
    Assert(!MediaReader.IsAppleMusic("Fake.AppleMusic.exe"));
    return Task.CompletedTask;
});
await Check("Apple Music's combined artist/album field is separated", () =>
{
    var track = MediaReader.ParseTrack("Pasażer", "Pidżama Porno — Futurista", "", "Pidżama Porno — Futurista");
    Assert(track.Artist == "Pidżama Porno" && track.Album == "Futurista" && track.AlbumArtist == "Pidżama Porno");
    var separate = MediaReader.ParseTrack("Song", "Artist", "Album", "Album Artist");
    Assert(separate.Artist == "Artist" && separate.AlbumArtist == "Album Artist");
    var knownAlbum = MediaReader.ParseTrack("Song", "Artist — Album", "Album", "Artist");
    Assert(knownAlbum.Artist == "Artist");
    return Task.CompletedTask;
});
await Check("Presence retains title/artist/cover and omits unavailable timing", () =>
{
    var payload = PresencePayload.Create(new("Song", "Artist", "Hidden album", "Hidden album artist"), "https://example.com/cover.jpg");
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
    Assert(payload.Count == 6 && json.RootElement.GetProperty("type").GetInt32() == 2);
    Assert(json.RootElement.GetProperty("name").GetString() == "Apple Music");
    Assert(json.RootElement.GetProperty("status_display_type").GetInt32() == 0);
    Assert(json.RootElement.GetProperty("details").GetString() == "Song");
    Assert(json.RootElement.GetProperty("state").GetString() == "Artist");
    Assert(json.RootElement.GetProperty("assets").EnumerateObject().Count() == 1);
    Assert(PresencePayload.Create(new("x", "", "", ""), null).Count == 4);
    Assert(PresencePayload.Limit(new string('a', 127) + "🎵z").Length == 127);
    return Task.CompletedTask;
});
await Check("Playback time uses the real timeline update time and RPC Unix seconds", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var track = new Track("Song", "Artist", "", "");
    var timeline = new PlaybackTimeline(TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(180), now.AddSeconds(-12));
    var clock = new PlaybackClock();
    var stamps = clock.Observe(new(track, true, Timeline: timeline), now);
    Assert(stamps == new ActivityTimestamps(1_699_999_940, 1_700_000_120));
    Assert(timeline.PositionAt(now, true) == TimeSpan.FromSeconds(60));
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(PresencePayload.Create(track, null, stamps)));
    Assert(json.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64() == 1_699_999_940);
    Assert(json.RootElement.GetProperty("timestamps").GetProperty("end").GetInt64() == 1_700_000_120);
    Assert(!json.RootElement.TryGetProperty("buttons", out _) && !json.RootElement.TryGetProperty("album", out _));
    return Task.CompletedTask;
});
await Check("Nonzero timeline start and playback speed are handled", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var timeline = PlaybackTimeline.FromSession(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(245), TimeSpan.FromSeconds(65), now.AddSeconds(-10), 1);
    Assert(timeline.Duration == TimeSpan.FromSeconds(240));
    Assert(timeline.PositionAt(now, true) == TimeSpan.FromSeconds(70));
    Assert(timeline.Timestamps(now)!.End == now.ToUnixTimeSeconds() + 170);
    var fast = new PlaybackTimeline(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180), now, 2);
    Assert(fast.Timestamps(now) == new ActivityTimestamps(now.ToUnixTimeSeconds() - 30, now.ToUnixTimeSeconds() + 60));
    Assert(fast.PositionAt(now.AddSeconds(5), true) == TimeSpan.FromSeconds(70));
    Assert(fast.PositionAt(now.AddSeconds(5), false) == TimeSpan.FromSeconds(60));
    return Task.CompletedTask;
});
await Check("Clock stays stable during playback and rounding, and resyncs on seek/repeat/resume", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var track = new Track("Song", "Artist", "", "");
    var clock = new PlaybackClock();
    MediaState At(int position, int since) => new(track, true, Timeline: new(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(180), now.AddSeconds(since)));
    var start = clock.Observe(At(30, 0), now);
    Assert(clock.Observe(At(32, 2), now.AddSeconds(2)) == start);
    Assert(clock.Observe(At(33, 4), now.AddSeconds(4)) == start);
    var seek = clock.Observe(At(100, 6), now.AddSeconds(6));
    Assert(seek!.End == now.ToUnixTimeSeconds() + 86);
    Assert(clock.Observe(At(100, 8) with { Playing = false }, now.AddSeconds(8)) is null);
    var resume = clock.Observe(At(100, 20), now.AddSeconds(20));
    Assert(resume!.End == now.ToUnixTimeSeconds() + 100);
    var repeat = clock.Observe(At(0, 100), now.AddSeconds(100));
    Assert(repeat!.End == now.ToUnixTimeSeconds() + 280);
    var other = clock.Observe(At(0, 102) with { Track = new("Other song", "Artist", "", "") }, now.AddSeconds(102));
    Assert(other!.Start == now.ToUnixTimeSeconds() + 102);
    return Task.CompletedTask;
});
await Check("Displayed seconds keep an even cadence despite jittery metadata polls", () =>
{
    var origin = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var time = new ManualTimeProvider(origin);
    var display = new PlaybackDisplayClock(time);
    var rpcClock = new PlaybackClock();
    var track = new Track("Song", "Artist", "", "");
    var initial = new MediaState(track, true, Timeline: new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(180), origin.AddMilliseconds(-270)));
    Assert(display.Observe(initial, rpcClock.Observe(initial, origin)));
    var previous = display.Read()!.Value.ElapsedSeconds;
    var changes = new List<int>();
    for (int ms = 25; ms <= 9000; ms += 25)
    {
        time.Advance(TimeSpan.FromMilliseconds(25));
        if (ms is 2150 or 4325 or 6175 or 8250)
        {
            double noise = ms switch { 2150 => -0.65, 4325 => 0.8, 6175 => -0.7, _ => 0.55 };
            var noisy = new MediaState(track, true, Timeline: new(TimeSpan.FromSeconds(12.27 + ms / 1000.0 + noise),
                TimeSpan.FromSeconds(180.01), time.GetUtcNow(), 1.000001));
            Assert(!display.Observe(noisy, rpcClock.Observe(noisy, time.GetUtcNow())));
        }
        var reading = display.Read()!.Value;
        if (reading.ElapsedSeconds != previous)
        {
            Assert(reading.ElapsedSeconds == previous + 1);
            Assert(reading.ElapsedSeconds + reading.RemainingSeconds == 180);
            changes.Add(ms);
            previous = reading.ElapsedSeconds;
        }
    }
    Assert(changes.Count == 9);
    Assert(changes.Zip(changes.Skip(1), (a, b) => b - a).All(ms => ms == 1000));
    return Task.CompletedTask;
});
await Check("Fractional song durations update elapsed and remaining together", () =>
{
    var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    var display = new PlaybackDisplayClock(time);
    var media = new MediaState(new("Song", "Artist", "", ""), true,
        Timeline: new(TimeSpan.FromSeconds(12.27), TimeSpan.FromSeconds(180.4), time.GetUtcNow()));
    display.Observe(media, media.Timeline!.Timestamps(time.GetUtcNow()));
    var previous = display.Read()!.Value;
    var changes = new List<int>();
    for (int ms = 25; ms <= 8000; ms += 25)
    {
        time.Advance(TimeSpan.FromMilliseconds(25));
        var reading = display.Read()!.Value;
        Assert(reading.ElapsedSeconds + reading.RemainingSeconds == reading.DurationSeconds);
        if (reading != previous) { changes.Add(ms); previous = reading; }
    }
    Assert(changes.Count == 8);
    Assert(changes.Zip(changes.Skip(1), (a, b) => b - a).All(ms => ms == 1000));
    return Task.CompletedTask;
});
await Check("Display clock freezes on pause and resyncs on seeks, repeats, and track changes", () =>
{
    var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    var display = new PlaybackDisplayClock(time);
    var rpcClock = new PlaybackClock();
    var track = new Track("Song", "Artist", "", "");
    MediaState At(double position, bool playing = true) => new(track, playing, Timeline: new(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(180), time.GetUtcNow()));
    void Observe(MediaState media) => display.Observe(media, rpcClock.Observe(media, time.GetUtcNow()));
    Observe(At(30.2));
    time.Advance(TimeSpan.FromSeconds(5));
    Assert(display.Read()!.Value.ElapsedSeconds == 35);
    Observe(At(35.2, false));
    time.Advance(TimeSpan.FromSeconds(4));
    Assert(display.Read()!.Value.ElapsedSeconds == 35);
    Observe(At(90, false));
    Assert(display.Read()!.Value.ElapsedSeconds == 90);
    Observe(At(90));
    time.Advance(TimeSpan.FromSeconds(1));
    Assert(display.Read()!.Value.ElapsedSeconds == 91);
    Observe(At(130));
    Assert(display.Read()!.Value.ElapsedSeconds == 130);
    Observe(At(0));
    Assert(display.Read()!.Value.ElapsedSeconds == 0);
    Observe(At(0) with { Track = new("Other song", "Artist", "", "") });
    Assert(display.Read()!.Value.ElapsedSeconds == 0);
    time.Advance(TimeSpan.FromSeconds(200));
    Assert(display.Read()!.Value == new PlaybackReadout(180, 180, 0));
    Observe(new(null, false));
    Assert(display.Read() is null);
    return Task.CompletedTask;
});
await Check("Display cadence uses monotonic time across wall-clock changes", () =>
{
    var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    var display = new PlaybackDisplayClock(time);
    var media = new MediaState(new("Song", "Artist", "", ""), true,
        Timeline: new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(180), time.GetUtcNow()));
    display.Observe(media, media.Timeline!.Timestamps(time.GetUtcNow()));
    time.Advance(TimeSpan.FromSeconds(1));
    time.JumpWallClock(TimeSpan.FromHours(1));
    Assert(display.Read()!.Value.ElapsedSeconds == 31);
    time.Advance(TimeSpan.FromSeconds(1));
    Assert(display.Read()!.Value.ElapsedSeconds == 32);
    return Task.CompletedTask;
});
await Check("Unknown/live/invalid timing never invents a countdown", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var track = new Track("Song", "Artist", "", "");
    var clock = new PlaybackClock();
    foreach (var timeline in new PlaybackTimeline?[]
    {
        null,
        new(TimeSpan.Zero, TimeSpan.Zero, now),
        new(TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(180), now),
        new(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(180), now),
        new(TimeSpan.Zero, TimeSpan.MaxValue, now),
        new(TimeSpan.Zero, TimeSpan.FromSeconds(180), DateTimeOffset.MinValue),
        new(TimeSpan.Zero, TimeSpan.FromSeconds(180), now.AddMinutes(1)),
        new(TimeSpan.Zero, TimeSpan.FromSeconds(180), now, double.NaN),
        new(TimeSpan.Zero, TimeSpan.FromSeconds(180), now, 0)
    })
        Assert(clock.Observe(new(track, true, Timeline: timeline), now) is null);
    return Task.CompletedTask;
});
await Check("Settings reload and tolerate invalid JSON", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "AppleMusicPresence-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings { ApplicationId = "123456789012345678", Enabled = false, Country = "pl" };
        store.Save(settings);
        Assert(store.Load() == settings && store.Load().SafeCountry == "PL");
        Assert(!AppSettings.IsValidId("token-value") && !AppSettings.IsValidId(null));
        File.WriteAllText(path, "{bad-json");
        Assert(store.Load().ApplicationId == "");
    }
    finally { Directory.Delete(dir, true); }
    return Task.CompletedTask;
});
await Check("Cover matching rejects wrong artists, live versions, and non-Apple hosts", () =>
{
    using var json = JsonDocument.Parse("""
    {"results":[
      {"trackName":"Neon","artistName":"Other","collectionName":"Album","artworkUrl100":"https://is1-ssl.mzstatic.com/other/100x100bb.jpg"},
      {"trackName":"Neon (Live)","artistName":"Björk","collectionName":"Album","artworkUrl100":"https://is1-ssl.mzstatic.com/live/100x100bb.jpg"},
      {"trackName":"Neon","artistName":"Björk","collectionName":"Compilation","artworkUrl100":"https://is1-ssl.mzstatic.com/compilation/100x100bb.jpg"},
      {"trackName":"Neon","artistName":"Björk","collectionName":"Album","artworkUrl100":"https://is1-ssl.mzstatic.com/exact/100x100bb.jpg"},
      {"trackName":"Neon","artistName":"Björk","collectionName":"Album","artworkUrl100":"https://mzstatic.com.evil.test/not-apple/100x100bb.jpg"}
    ]}
    """);
    Assert(ArtworkResolver.Match(new("Neon", "Björk", "Album", ""), json.RootElement)?.Url == "https://is1-ssl.mzstatic.com/exact/512x512bb.jpg");
    Assert(ArtworkResolver.Match(new("Neon", "No match", "", ""), json.RootElement) is null);
    return Task.CompletedTask;
});
await Check("Artwork requests are encoded and cached", async () =>
{
    var handler = new FakeHttp();
    using var resolver = new ArtworkResolver(handler);
    var track = new Track("Song & more", "Artist", "Album", "");
    var first = await resolver.ResolveAsync(track, "PL", CancellationToken.None);
    var second = await resolver.ResolveAsync(track, "PL", CancellationToken.None);
    Assert(first == second && first is not null && handler.Requests.Count == 1);
    Assert(handler.Requests[0].Query.Contains("%26") && handler.Requests[0].Query.Contains("country=PL"));
});
await Check("IPC handles fragmented frames, ping/pong, acknowledgement, and clear", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var pipeName = "AppleMusicPresence-test-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var mock = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(timeout.Token);
        var handshake = await ReadFrame(server, timeout.Token);
        Assert(handshake.Opcode == 0 && handshake.Json.RootElement.GetProperty("client_id").GetString() == "123456789012345678");
        handshake.Json.Dispose();
        await SendFrame(server, 1, new { evt = "READY", data = new { v = 1 } }, timeout.Token, fragmented: true);
        var set = await ReadFrame(server, timeout.Token);
        Assert(set.Json.RootElement.GetProperty("cmd").GetString() == "SET_ACTIVITY");
        Assert(set.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("name").GetString() == "Apple Music");
        Assert(set.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("status_display_type").GetInt32() == 0);
        Assert(set.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString() == "Track");
        Assert(set.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("timestamps").GetProperty("end").GetInt64() == 1_700_000_180);
        await SendFrame(server, 3, new { ping = "test" }, timeout.Token);
        var pong = await ReadFrame(server, timeout.Token);
        Assert(pong.Opcode == 4 && pong.Json.RootElement.GetProperty("ping").GetString() == "test");
        pong.Json.Dispose();
        await SendFrame(server, 1, new { cmd = "SET_ACTIVITY", nonce = set.Json.RootElement.GetProperty("nonce").GetString(), evt = (string?)null, data = new { } }, timeout.Token);
        set.Json.Dispose();
        var clear = await ReadFrame(server, timeout.Token);
        Assert(clear.Json.RootElement.GetProperty("args").GetProperty("activity").ValueKind == JsonValueKind.Null);
        await SendFrame(server, 1, new { nonce = clear.Json.RootElement.GetProperty("nonce").GetString(), evt = (string?)null }, timeout.Token);
        clear.Json.Dispose();
    });
    await using var rpc = new DiscordRpc([pipeName]);
    await rpc.ConnectAsync("123456789012345678", timeout.Token);
    await rpc.SetActivityAsync(PresencePayload.Create(new("Track", "Artist", "", ""), null, new(1_700_000_000, 1_700_000_180)), timeout.Token);
    await rpc.SetActivityAsync(null, timeout.Token);
    await mock;
});
await Check("IPC errors reach the caller and closed pipes are detected", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var pipeName = "AppleMusicPresence-test-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var mock = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(timeout.Token);
        var handshake = await ReadFrame(server, timeout.Token);
        handshake.Json.Dispose();
        await SendFrame(server, 1, new { evt = "READY" }, timeout.Token);
        var request = await ReadFrame(server, timeout.Token);
        await SendFrame(server, 1, new { evt = "ERROR", nonce = request.Json.RootElement.GetProperty("nonce").GetString(), data = new { code = 4000, message = "Test rejection" } }, timeout.Token);
        request.Json.Dispose();
    });
    await using var rpc = new DiscordRpc([pipeName]);
    await rpc.ConnectAsync("123456789012345678", timeout.Token);
    bool rejected = false;
    try { await rpc.SetActivityAsync(new { details = "x" }, timeout.Token); }
    catch (RpcException e) { rejected = e.Message.Contains("Test rejection"); }
    Assert(rejected);
    await mock;
    server.Dispose();
    for (int i = 0; i < 100 && rpc.Connected; i++) await Task.Delay(10, timeout.Token);
    Assert(!rpc.Connected);
});
await Check("Track changes drop stale covers; pause, resume, reconnect, disable work", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var a = new Track("First", "Artist", "Album", "");
    var b = new Track("Second", "Artist", "Other", "");
    var reader = new FakeMedia { State = new(a, true) };
    var rpc = new FakeRpc();
    var settings = new AppSettings { ApplicationId = "123456789012345678" };
    var step = 0;
    var service = new PresenceService(() => settings, snapshot =>
    {
        switch (step++)
        {
            case 0: Assert(snapshot.Live); reader.State = new(b, true); break;
            case 1: rpc.Connected = false; break;
            case 2: reader.State = new(b, false); break;
            case 3: Assert(!snapshot.Live && snapshot.Status == "Playback paused"); reader.State = new(b, true); break;
            case 4: settings = settings with { Enabled = false }; break;
            case 5: Assert(!snapshot.Live); timeout.Cancel(); break;
        }
    }, () => reader, () => new FakeArtwork(), () => rpc, TimeSpan.FromMilliseconds(10));
    await service.RunAsync(timeout.Token);
    Assert(step == 6 && rpc.Connects == 3 && rpc.Activities.Count == 6);
    Assert(rpc.Activities[0]!.Contains("first.jpg"));
    Assert(rpc.Activities[1]!.Contains("Second") && !rpc.Activities[1]!.Contains("assets"));
    Assert(rpc.Activities[2]!.Contains("Second"));
    Assert(rpc.Activities[3] is null && rpc.Activities[5] is null);
});
await Check("Service republishes seeks/resumes/repeats but does not republish a ticking clock", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var origin = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var now = origin;
    var track = new Track("First", "Artist", "Album", "");
    var reader = new FakeMedia();
    var rpc = new FakeRpc();
    var settings = new AppSettings { ApplicationId = "123456789012345678" };
    MediaState At(int position, int since) => new(track, true, Timeline: new(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(180), origin.AddSeconds(since)));
    reader.State = At(30, 0);
    var step = 0;
    var service = new PresenceService(() => settings, snapshot =>
    {
        switch (step++)
        {
            case 0: reader.State = At(32, 2); now = origin.AddSeconds(2); break;
            case 1: Assert(rpc.Activities.Count == 1); reader.State = At(100, 4); now = origin.AddSeconds(4); break;
            case 2: Assert(rpc.Activities.Count == 2); reader.State = At(100, 6) with { Playing = false }; now = origin.AddSeconds(6); break;
            case 3: Assert(!snapshot.Live); reader.State = At(100, 8); now = origin.AddSeconds(8); break;
            case 4: reader.State = At(0, 10); now = origin.AddSeconds(10); break;
            case 5: reader.State = new(new("Second", "Artist", "Other", ""), true, Timeline: new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(200), origin.AddSeconds(12))); now = origin.AddSeconds(12); break;
            case 6: timeout.Cancel(); break;
        }
    }, () => reader, () => new FakeArtwork(), () => rpc, TimeSpan.FromMilliseconds(10), () => now);
    await service.RunAsync(timeout.Token);
    Assert(step == 7 && rpc.Activities.Count == 7);
    long End(int index) { using var json = JsonDocument.Parse(rpc.Activities[index]!); return json.RootElement.GetProperty("timestamps").GetProperty("end").GetInt64(); }
    Assert(End(0) == origin.ToUnixTimeSeconds() + 150);
    Assert(End(1) == origin.ToUnixTimeSeconds() + 84);
    Assert(rpc.Activities[2] is null);
    Assert(End(3) == origin.ToUnixTimeSeconds() + 88);
    Assert(End(4) == origin.ToUnixTimeSeconds() + 190);
    Assert(End(5) == origin.ToUnixTimeSeconds() + 202 && rpc.Activities[5]!.Contains("Second"));
    Assert(rpc.Activities[6] is null);
});
Console.WriteLine($"\n{passed} checks passed.");
return 0;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + "\n" + e); Environment.Exit(1); }
}
static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
static async Task<(int Opcode, JsonDocument Json)> ReadFrame(Stream pipe, CancellationToken ct)
{
    var header = new byte[8];
    await pipe.ReadExactlyAsync(header, ct);
    var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
    await pipe.ReadExactlyAsync(body, ct);
    return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonDocument.Parse(body));
}
static async Task SendFrame(Stream pipe, int opcode, object payload, CancellationToken ct, bool fragmented = false)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
    var frame = new byte[8 + bytes.Length];
    BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), bytes.Length);
    bytes.CopyTo(frame, 8);
    if (fragmented) { foreach (var b in frame) await pipe.WriteAsync(new byte[] { b }, ct); }
    else await pipe.WriteAsync(frame, ct);
    await pipe.FlushAsync(ct);
}
sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset utc = now;
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    public override long GetTimestamp() => timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed) { utc += elapsed; timestamp += elapsed.Ticks; }
    public void JumpWallClock(TimeSpan offset) => utc += offset;
}

sealed class FakeHttp : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
        {"results":[{"trackName":"Song & more","artistName":"Artist","collectionName":"Album","artworkUrl100":"https://is1-ssl.mzstatic.com/cover/100x100bb.jpg"}]}
        """, Encoding.UTF8, "application/json") });
    }
}
sealed class FakeMedia : IMediaReader
{
    public MediaState State { get; set; } = new(null, false);
    public Task<MediaState> ReadAsync(CancellationToken ct) => Task.FromResult(State);
    public void Reset() { }
}
sealed class FakeArtwork : IArtworkResolver
{
    public async Task<Artwork?> ResolveAsync(Track track, string country, CancellationToken ct)
    {
        if (track.Title == "First") return new("https://is1-ssl.mzstatic.com/first.jpg", "");
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    }
    public void Dispose() { }
}
sealed class FakeRpc : IDiscordRpc
{
    public bool Connected { get; set; }
    public int Connects;
    public List<string?> Activities { get; } = [];
    public Task ConnectAsync(string id, CancellationToken ct) { Connected = true; Connects++; return Task.CompletedTask; }
    public Task SetActivityAsync(object? activity, CancellationToken ct) { Activities.Add(activity is null ? null : JsonSerializer.Serialize(activity)); return Task.CompletedTask; }
    public Task DisconnectAsync() { Connected = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
