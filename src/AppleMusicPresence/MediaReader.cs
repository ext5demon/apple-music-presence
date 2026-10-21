using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppleMusicPresence;

public sealed record Track(string Title, string Artist, string Album, string AlbumArtist)
{
    public string Key => $"{Title}\0{Artist}\0{Album}\0{AlbumArtist}";
}

public sealed record MediaState(Track? Track, bool Playing, byte[]? Thumbnail = null, PlaybackTimeline? Timeline = null);

public interface IMediaReader
{
    Task<MediaState> ReadAsync(CancellationToken ct);
    void Reset();
}

public sealed class MediaReader : IMediaReader
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private string? thumbnailKey;
    private byte[]? thumbnail;
    private DateTimeOffset thumbnailAttempt;

    public static bool IsAppleMusic(string source) =>
        source.StartsWith("AppleInc.AppleMusicWin_", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("AppleMusic.exe", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("AppleMusic", StringComparison.OrdinalIgnoreCase);

    public async Task<MediaState> ReadAsync(CancellationToken ct)
    {
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        var sessions = manager.GetSessions().Where(s => IsAppleMusic(s.SourceAppUserModelId)).ToList();
        var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? sessions.FirstOrDefault();
        if (session is null)
        {
            thumbnailKey = null;
            thumbnail = null;
            return new(null, false);
        }
        var props = await session.TryGetMediaPropertiesAsync().AsTask(ct);
        var title = props.Title?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title)) return new(null, false);
        var artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist?.Trim() ?? "" : props.Artist.Trim();
        var track = ParseTrack(title, artist, props.AlbumTitle?.Trim() ?? "", props.AlbumArtist?.Trim() ?? "");
        if (thumbnailKey != track.Key)
        {
            thumbnailKey = track.Key;
            thumbnail = null;
            thumbnailAttempt = DateTimeOffset.MinValue;
        }
        if (thumbnail is null && props.Thumbnail is not null && DateTimeOffset.UtcNow - thumbnailAttempt > TimeSpan.FromSeconds(15))
        {
            thumbnailAttempt = DateTimeOffset.UtcNow;
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync().AsTask(ct);
                if (stream.Size is > 0 and <= 8_000_000)
                {
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)stream.Size).AsTask(ct);
                    thumbnail = new byte[(int)stream.Size];
                    reader.ReadBytes(thumbnail);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Artwork is optional; text presence must continue working without it.
                AppLog.Error("Could not read media thumbnail", e);
            }
        }
        var playback = session.GetPlaybackInfo();
        var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlaybackTimeline? timeline = null;
        try
        {
            var properties = session.GetTimelineProperties();
            timeline = PlaybackTimeline.FromSession(properties.StartTime, properties.EndTime, properties.Position,
                properties.LastUpdatedTime, playback.PlaybackRate ?? 1);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A missing timeline must not interrupt song/artist/cover sharing.
        }
        return new(track, playing, thumbnail, timeline);
    }

    public void Reset() => manager = null;

    public static Track ParseTrack(string title, string artist, string album, string albumArtist)
    {
        // Apple Music for Windows currently puts "Artist — Album" in Artist and
        // AlbumArtist, leaving AlbumTitle empty. Keep the album out of Discord.
        const string separator = " — ";
        var combinedArtist = artist;
        if (string.IsNullOrWhiteSpace(album))
        {
            int index = artist.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && index + separator.Length < artist.Length)
            {
                album = artist[(index + separator.Length)..].Trim();
                artist = artist[..index].Trim();
            }
        }
        else if (artist.EndsWith(separator + album, StringComparison.OrdinalIgnoreCase))
            artist = artist[..^(separator.Length + album.Length)].Trim();
        if (albumArtist == combinedArtist) albumArtist = artist;
        return new(title.Trim(), artist.Trim(), album.Trim(), albumArtist.Trim());
    }

    public async Task<object> DiagnoseAsync(CancellationToken ct)
    {
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        var sources = manager.GetSessions().Select(s => new { Source = s.SourceAppUserModelId, AppleMusic = IsAppleMusic(s.SourceAppUserModelId), Status = s.GetPlaybackInfo().PlaybackStatus.ToString() }).ToArray();
        var state = await ReadAsync(ct);
        return new { Sessions = sources, state.Track, state.Playing, state.Timeline,
            Timestamps = new PlaybackClock().Observe(state, DateTimeOffset.UtcNow), ThumbnailBytes = state.Thumbnail?.Length ?? 0 };
    }
}
