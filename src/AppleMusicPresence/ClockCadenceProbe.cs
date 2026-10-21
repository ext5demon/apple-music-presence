using System.Diagnostics;
using System.Text.Json;

namespace AppleMusicPresence;

// Opt-in local QA: observe the actual label's change events for ten seconds.
// No song names, account details, or routine listening history are recorded.
internal static class ClockCadenceProbe
{
    public static void Attach(MainForm window, string destination)
    {
        var label = window.Controls.Find("PlaybackTime", true).OfType<Label>().Single();
        var stopwatch = Stopwatch.StartNew();
        var changes = new List<(double AtMs, string Display)>();
        EventHandler record = (_, _) => changes.Add((stopwatch.Elapsed.TotalMilliseconds, label.Text));
        label.TextChanged += record;
        var finish = new System.Windows.Forms.Timer { Interval = 10_000 };
        finish.Tick += (_, _) =>
        {
            finish.Stop();
            label.TextChanged -= record;
            var samples = changes.Select(c => new { c.AtMs, c.Display }).ToArray();
            var intervals = changes.Zip(changes.Skip(1), (a, b) => b.AtMs - a.AtMs).ToArray();
            try { File.WriteAllText(destination, JsonSerializer.Serialize(new { Samples = samples, IntervalsMs = intervals }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { AppLog.Error("Could not write opt-in clock cadence report", e); }
            finish.Dispose();
        };
        window.Disposed += (_, _) => finish.Dispose();
        finish.Start();
    }
}
