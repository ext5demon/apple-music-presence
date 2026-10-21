namespace AppleMusicPresence;

public readonly record struct PlaybackReadout(long ElapsedSeconds, long DurationSeconds, long RemainingSeconds);

public sealed class PlaybackDisplayClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private bool available;
    private bool playing;
    private string? trackKey;
    private ActivityTimestamps? synchronizedTimestamps;
    private long sampledAt;
    private double sampledPosition;
    private double duration;
    private double rate;

    public bool Observe(MediaState media, ActivityTimestamps? timestamps)
    {
        var now = time.GetUtcNow();
        if (media.Track is null || media.Timeline is not { } timeline || !timeline.IsValid(now))
        {
            var changed = available;
            available = false;
            return changed;
        }

        bool resync = !available || trackKey != media.Track.Key || playing != media.Playing ||
            synchronizedTimestamps != timestamps || Math.Abs(duration - timeline.Duration.TotalSeconds) > 1 || Math.Abs(rate - timeline.Rate) > 0.0001 ||
            (!media.Playing && Math.Abs(sampledPosition - timeline.Position.TotalSeconds) > 0.01);
        if (!resync) return false;

        // Share the RPC clock's synchronization decisions. Normal metadata polls
        // cannot move this anchor, even if SMTC positions are rounded or jittery.
        sampledPosition = timeline.PositionAt(now, media.Playing).TotalSeconds;
        sampledAt = time.GetTimestamp();
        duration = timeline.Duration.TotalSeconds;
        rate = timeline.Rate;
        playing = media.Playing;
        trackKey = media.Track.Key;
        synchronizedTimestamps = timestamps;
        available = true;
        return true;
    }

    public PlaybackReadout? Read()
    {
        if (!available) return null;
        // TimeProvider's system timestamp uses the monotonic performance counter.
        // A delayed paint or wall-clock adjustment does not change the tick cadence.
        double elapsed = playing ? time.GetElapsedTime(sampledAt, time.GetTimestamp()).TotalSeconds * rate : 0;
        double position = Math.Clamp(sampledPosition + elapsed, 0, duration);
        long totalSeconds = (long)Math.Ceiling(duration);
        long elapsedSeconds = position >= duration ? totalSeconds : (long)Math.Floor(position);
        // Both labels use the same whole-second boundary. Fractional durations
        // must not make "elapsed" and "remaining" change at different instants.
        long remainingSeconds = (long)Math.Ceiling((totalSeconds - elapsedSeconds) / rate);
        return new(elapsedSeconds, totalSeconds, remainingSeconds);
    }
}
