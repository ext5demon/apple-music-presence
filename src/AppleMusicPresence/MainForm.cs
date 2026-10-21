using System.Diagnostics;

namespace AppleMusicPresence;

public sealed class MainForm : Form
{
    private readonly Func<AppSettings> getSettings;
    private readonly Action<AppSettings> saveSettings;
    private readonly TextBox applicationId;
    private readonly CheckBox share;
    private readonly CheckBox startup;
    private readonly Label title;
    private readonly Label artist;
    private readonly Label time;
    private readonly Label status;
    private readonly Label hint;
    private readonly PictureBox cover;
    private readonly ToolStripStatusLabel message;
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 50 };
    private readonly PlaybackDisplayClock displayClock = new();
    private byte[]? lastThumbnail;
    private string? lastTrackKey;
    private bool updating;
    private bool allowExit;
    public event Action? QuitRequested;

    public MainForm(Func<AppSettings> getSettings, Action<AppSettings> saveSettings)
    {
        this.getSettings = getSettings;
        this.saveSettings = saveSettings;
        Text = "Apple Music Presence";
        ClientSize = new Size(480, 486);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadIcon();

        var connection = new GroupBox
        {
            Text = "Discord connection", Location = new Point(12, 12), Size = new Size(456, 132), TabIndex = 0
        };
        Controls.Add(connection);
        AddLabel(connection, "Application ID:", 12, 27, 94, 20);
        applicationId = new TextBox
        {
            Location = new Point(108, 23), Size = new Size(214, 23),
            MaxLength = 20, AccessibleName = "Discord Application ID", TabIndex = 0
        };
        connection.Controls.Add(applicationId);
        var connect = new Button
        {
            Text = "&Save && connect", Location = new Point(332, 21), Size = new Size(112, 27),
            UseVisualStyleBackColor = true, TabIndex = 1
        };
        connect.Click += (_, _) => SaveId();
        connection.Controls.Add(connect);
        AcceptButton = connect;

        var portal = new LinkLabel
        {
            Text = "Discord Developer Portal", Location = new Point(12, 58), Size = new Size(300, 20),
            AutoSize = true, TabIndex = 2
        };
        portal.LinkClicked += (_, _) => OpenUrl("https://discord.com/developers/applications");
        connection.Controls.Add(portal);
        AddLabel(connection,
            "Create an application named Apple Music, then paste its Application ID.\nNo bot, token, or Apple Music login needed.",
            12, 84, 432, 36);

        var playing = new GroupBox
        {
            Text = "Now playing", Location = new Point(12, 154), Size = new Size(456, 112), TabIndex = 1
        };
        Controls.Add(playing);
        cover = new PictureBox
        {
            Location = new Point(12, 24), Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window,
            AccessibleName = "Song cover", TabStop = false
        };
        playing.Controls.Add(cover);
        title = AddLabel(playing, "No song playing", 100, 28, 344, 20);
        artist = AddLabel(playing, "", 100, 56, 344, 20);
        time = AddLabel(playing, "Unavailable", 100, 82, 344, 20);
        time.Name = "PlaybackTime";

        var state = new GroupBox
        {
            Text = "Status", Location = new Point(12, 276), Size = new Size(456, 76), TabIndex = 2
        };
        Controls.Add(state);
        status = AddLabel(state, "One-time setup", 12, 23, 432, 20);
        hint = AddLabel(state, "Add your Discord Application ID above.", 12, 45, 432, 20);

        share = new CheckBox
        {
            Text = "Share my music on &Discord", Location = new Point(14, 362), Size = new Size(440, 23),
            UseVisualStyleBackColor = true, TabIndex = 3
        };
        startup = new CheckBox
        {
            Text = "Start with &Windows", Location = new Point(14, 389), Size = new Size(440, 23),
            UseVisualStyleBackColor = true, TabIndex = 4
        };
        Controls.Add(share);
        Controls.Add(startup);
        share.CheckedChanged += (_, _) => { if (!updating) TrySave(getSettings() with { Enabled = share.Checked }); };
        startup.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            var previous = getSettings().StartWithWindows;
            var desired = startup.Checked;
            try
            {
                WindowsStartup.SetEnabled(desired);
                if (!TrySave(getSettings() with { StartWithWindows = desired })) WindowsStartup.SetEnabled(previous);
            }
            catch (Exception e) { ShowError("Couldn't change startup: " + e.Message); RefreshSettings(); }
        };

        var background = new Button
        {
            Text = "Run in &background", Location = new Point(216, 425), Size = new Size(146, 28),
            UseVisualStyleBackColor = true, TabIndex = 5
        };
        background.Click += (_, _) => Hide();
        Controls.Add(background);
        CancelButton = background;
        var quit = new Button
        {
            Text = "&Quit", Location = new Point(374, 425), Size = new Size(94, 28),
            UseVisualStyleBackColor = true, TabIndex = 6
        };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        Controls.Add(quit);

        var bar = new StatusStrip { SizingGrip = false, ShowItemToolTips = true, TabStop = false };
        message = new ToolStripStatusLabel("Closing the window keeps the app in the system tray.")
        {
            Spring = true, TextAlign = ContentAlignment.MiddleLeft
        };
        bar.Items.Add(message);
        Controls.Add(bar);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            {
                allowExit = true;
                QuitRequested?.Invoke();
            }
            else if (!allowExit) { e.Cancel = true; Hide(); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        RefreshSettings();
        clockTimer.Tick += (_, _) => UpdateTimeReadout();
        VisibleChanged += (_, _) =>
        {
            if (Disposing || IsDisposed) return;
            clockTimer.Enabled = Visible;
            if (Visible) UpdateTimeReadout();
        };
    }

    private static Label AddLabel(Control parent, string text, int x, int y, int width, int height)
    {
        var label = new Label
        {
            Text = text, Location = new Point(x, y), Size = new Size(width, height),
            AutoEllipsis = true, UseMnemonic = false
        };
        parent.Controls.Add(label);
        return label;
    }

    private void SaveId()
    {
        var id = applicationId.Text.Trim();
        if (!AppSettings.IsValidId(id))
        {
            ShowError("Enter the 17–20 digit Application ID from Discord's developer portal.");
            applicationId.Focus();
            return;
        }
        if (TrySave(getSettings() with { ApplicationId = id })) SetMessage("Saved. Play a song with Discord desktop open.");
    }

    private bool TrySave(AppSettings settings)
    {
        try
        {
            saveSettings(settings);
            SetMessage("Settings saved.");
            RefreshSettings();
            return true;
        }
        catch (Exception e) { ShowError("Couldn't save settings: " + e.Message); RefreshSettings(); return false; }
    }

    public void RefreshSettings()
    {
        updating = true;
        try
        {
            var s = getSettings();
            if (!applicationId.Focused) applicationId.Text = s.ApplicationId;
            share.Checked = s.Enabled;
            startup.Checked = s.StartWithWindows;
        }
        finally { updating = false; }
    }

    public void UpdatePresence(PresenceSnapshot snapshot)
    {
        status.Text = snapshot.Status;
        hint.Text = snapshot.Hint;
        tips.SetToolTip(hint, snapshot.Hint);
        title.Text = snapshot.Media.Track?.Title ?? "No song playing";
        artist.Text = snapshot.Media.Track?.Artist ?? "";
        tips.SetToolTip(title, title.Text);
        tips.SetToolTip(artist, artist.Text);
        if (displayClock.Observe(snapshot.Media, snapshot.Timestamps)) UpdateTimeReadout();
        if (!ReferenceEquals(lastThumbnail, snapshot.Media.Thumbnail) || lastTrackKey != snapshot.Media.Track?.Key)
        {
            Image? image = null;
            try
            {
                if (snapshot.Media.Thumbnail is { Length: > 0 } bytes)
                {
                    using var stream = new MemoryStream(bytes);
                    using var decoded = Image.FromStream(stream);
                    image = new Bitmap(decoded);
                }
            }
            catch (ArgumentException) { }
            var old = cover.Image;
            cover.Image = image;
            old?.Dispose();
            lastThumbnail = snapshot.Media.Thumbnail;
            lastTrackKey = snapshot.Media.Track?.Key;
        }
    }

    private void UpdateTimeReadout()
    {
        string text = displayClock.Read() is { } reading
            ? $"{FormatTime(reading.ElapsedSeconds)} / {FormatTime(reading.DurationSeconds)} ({FormatTime(reading.RemainingSeconds)} left)"
            : "Unavailable";
        if (time.Text != text) time.Text = text;
    }

    private static string FormatTime(long seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    private void SetMessage(string text) { message.Text = text; message.ToolTipText = text; }
    public void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void CloseForExit() { allowExit = true; Close(); }
    public void ShowError(string text) => SetMessage(text);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { AppLog.Error("Could not open browser", e); }
    }

    public static Icon LoadIcon() => (Icon)SystemIcons.Application.Clone();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clockTimer.Dispose(); tips.Dispose(); cover?.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
