namespace AppleMusicPresence;

// Discord's local RPC uses Unix seconds for timestamps (the gateway uses milliseconds).
public sealed record ActivityTimestamps(long Start, long End);

public sealed record PlaybackTimeline(TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAt, double Rate = 1)
{
    public static PlaybackTimeline FromSession(TimeSpan start, TimeSpan end, TimeSpan position, DateTimeOffset updatedAt, double rate) =>
        new(position - start, end - start, updatedAt, rate);

    public bool IsValid(DateTimeOffset now) =>
        Duration > TimeSpan.Zero && Duration <= TimeSpan.FromDays(7) &&
        Position >= TimeSpan.Zero && Position <= Duration &&
        double.IsFinite(Rate) && Rate is >= 0.1 and <= 16 &&
        UpdatedAt >= DateTimeOffset.UnixEpoch && UpdatedAt <= now.AddSeconds(5);

    public TimeSpan PositionAt(DateTimeOffset now, bool playing)
    {
        var seconds = Position.TotalSeconds;
        if (playing) seconds += Math.Max(0, (now - UpdatedAt).TotalSeconds) * Rate;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds));
    }

    public ActivityTimestamps? Timestamps(DateTimeOffset now)
    {
        if (!IsValid(now)) return null;
        var start = UpdatedAt.AddSeconds(-Position.TotalSeconds / Rate);
        var end = start.AddSeconds(Duration.TotalSeconds / Rate);
        return new(start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
    }
}

public sealed class PlaybackClock
{
    private string? trackKey;
    private ActivityTimestamps? anchor;

    public ActivityTimestamps? Observe(MediaState media, DateTimeOffset now)
    {
        var candidate = media.Playing && media.Track is not null ? media.Timeline?.Timestamps(now) : null;
        if (candidate is null)
        {
            trackKey = null;
            anchor = null;
            return null;
        }
        // Small SMTC rounding differences must not continuously republish the activity.
        // A seek, repeat, resume, duration change, or different track gets a fresh anchor.
        if (trackKey != media.Track!.Key || anchor is null ||
            Math.Abs(candidate.Start - anchor.Start) > 2 || Math.Abs(candidate.End - anchor.End) > 2)
        {
            trackKey = media.Track.Key;
            anchor = candidate;
        }
        return anchor;
    }
}
