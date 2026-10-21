using System.Text.Json;

namespace AppleMusicPresence;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            // Keep WinRT calls and their continuations on one message-pumping thread.
            using var diagnosticContext = new ApplicationContext();
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            dispatcher.BeginInvoke(async () =>
            {
                try { await DiagnoseAsync(args[1]); }
                finally { diagnosticContext.ExitThread(); }
            });
            Application.Run(diagnosticContext);
            return;
        }
        using var single = new Mutex(true, @"Local\AppleMusicPresence.SingleInstance", out bool first);
        using var open = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AppleMusicPresence.ShowWindow");
        if (!first) { open.Set(); return; }
        Application.ThreadException += (_, e) => AppLog.Error("UI error", e.Exception);
        var cadenceReport = args.Length == 2 && args[0] == "--clock-check" ? args[1] : null;
        try { Application.Run(new TrayApplication(args.Contains("--background"), open, cadenceReport)); }
        catch (Exception e)
        {
            AppLog.Error("Application error", e);
            MessageBox.Show("Apple Music Presence couldn't start. Details are in:\n" + SettingsStore.DataDirectory + "\\app.log", "Apple Music Presence", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task DiagnoseAsync(string output)
    {
        object result;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var reader = new MediaReader();
            var media = await reader.DiagnoseAsync(ct.Token);
            var current = await reader.ReadAsync(ct.Token);
            using var resolver = new ArtworkResolver();
            var art = current.Track is null ? null : await resolver.ResolveAsync(current.Track, new SettingsStore().Load().SafeCountry, ct.Token);
            result = new { Success = true, Media = media, Artwork = art };
        }
        catch (Exception e) { result = new { Success = false, Error = e.ToString() }; }
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class TrayApplication : ApplicationContext
{
    private AppSettings settings;
    private readonly SettingsStore store = new();
    private readonly MainForm window;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem shareMenu;
    private readonly CancellationTokenSource stop = new();
    private readonly Task serviceTask;
    private readonly RegisteredWaitHandle openRegistration;
    private bool exiting;

    public TrayApplication(bool background, EventWaitHandle openEvent, string? cadenceReport = null)
    {
        settings = store.Load() with { StartWithWindows = WindowsStartup.IsEnabled() };
        window = new MainForm(() => Volatile.Read(ref settings), Save);
        _ = window.Handle; // Establish a UI target even when starting hidden.
        if (cadenceReport is not null) ClockCadenceProbe.Attach(window, cadenceReport);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Apple Music Presence", null, (_, _) => window.Reveal());
        shareMenu = new ToolStripMenuItem("Share music on Discord") { Checked = settings.Enabled, CheckOnClick = false };
        shareMenu.Click += (_, _) =>
        {
            try { Save(settings with { Enabled = !settings.Enabled }); }
            catch (Exception e) { window.Reveal(); window.ShowError(e.Message); }
        };
        menu.Items.Add(shareMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray = new NotifyIcon { Icon = global::AppleMusicPresence.MainForm.LoadIcon(), Text = "Apple Music Presence", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => window.Reveal();
        window.QuitRequested += Quit;
        openRegistration = ThreadPool.RegisterWaitForSingleObject(openEvent, (_, _) => Post(window.Reveal), null, Timeout.Infinite, false);
        var service = new PresenceService(() => Volatile.Read(ref settings), snapshot => Post(() =>
        {
            window.UpdatePresence(snapshot);
            tray.Text = "Apple Music Presence • " + snapshot.Status;
        }));
        // The WinRT media session proxies can be apartment-bound. Await on the
        // WinForms context; all I/O stays asynchronous, including while hidden.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        serviceTask = RunMonitorAsync();
        if (!background || !AppSettings.IsValidId(settings.ApplicationId)) window.Show();

        async Task RunMonitorAsync()
        {
            // Recover the monitor if Windows temporarily invalidates a media session.
            while (!stop.IsCancellationRequested)
            {
                try { await service.RunAsync(stop.Token); break; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    AppLog.Error("Presence monitor restarting", e);
                    Post(() => window.UpdatePresence(new(new(null, false), "Reconnecting", "The monitor is restarting…", false)));
                    try { await Task.Delay(5000, stop.Token); } catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    private void Save(AppSettings next)
    {
        store.Save(next);
        Volatile.Write(ref settings, next);
        shareMenu.Checked = next.Enabled;
        window.RefreshSettings();
    }
    private void Post(Action action)
    {
        if (exiting || window.IsDisposed) return;
        try { window.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private async void Quit()
    {
        if (exiting) return;
        exiting = true;
        stop.Cancel();
        tray.Visible = false;
        openRegistration.Unregister(null);
        try { await serviceTask.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception e) { AppLog.Error("Shutdown", e); }
        window.CloseForExit();
        tray.Dispose();
        ExitThread();
    }
}
