# Apple Music Presence

A small Windows tray app that shows your **song title, artist, cover, and playback time** in Discord. No album text or buttons are added to the presence.

The settings window uses standard Windows controls: group boxes, buttons, text fields, checkboxes, and a status bar.

## Start here

1. Put `AppleMusicPresence.exe` in a folder you want to keep, then double-click it. No runtime installation or administrator access is needed.
2. Open [Discord's developer portal](https://discord.com/developers/applications), choose **New Application**, and name it **Apple Music**. The app also explicitly requests the display name **Apple Music** and a listening activity, so the heading reads “Listening to Apple Music.”
3. In that application's **General Information**, copy the **Application ID**. Paste it into Apple Music Presence and click **Save & connect**. You do not need to create a bot or copy any token, client secret, or public key.
4. Keep Discord desktop open and signed in. Play music in the **Apple Music Windows app**.
5. Click **Run in background**. Closing or minimizing the window also hides it in the system tray. Double-click its default Windows application icon to reopen it; right-click the icon to turn sharing off or quit.

“Live on Discord” means Discord acknowledged the update. To make activities visible to other people, enable activity sharing in Discord's activity privacy settings. This app does not change Discord's settings for you.

**Start with Windows** is optional and off by default. When enabled, it starts the app hidden after you sign in. Turn it off before moving the executable, or turn it off and on again after moving it so the saved path is updated.

## Behavior

- Reads the official Apple Music app's Windows media session, even when another media app is open.
- Checks Apple Music every two seconds, with no console window. The visible clock advances from a monotonic timer, changing once per playback second. Normal media polling does not reset it; seeking, pausing, resuming, and track changes resynchronize it. Reconnects when Discord restarts.
- Windows media queries run on a dedicated thread so they cannot hold up the window's clock or controls.
- Clears the presence when music is paused/stopped, Apple Music closes, sharing is turned off, or this app quits.
- Sends start/end timestamps from Apple Music's Windows playback timeline so Discord can show the song's progress and remaining time. Seeking, repeating a song, and resuming playback update the clock. Small timing differences are ignored to avoid unnecessary Discord updates.
- When Apple Music does not provide a valid duration/timeline (for example, some live streams), the presence continues without a timer.
- Uses the local album thumbnail in its preview. For Discord, it matches the song and artist against Apple's public catalog to find a public artwork URL. Album metadata helps pick the correct release, but is not shown in the presence.
- If matching artwork is unavailable (including some imported/local tracks or region-specific releases), song and artist still work. It does not upload your local artwork.
- Long song/artist names are limited to Discord's 128-character fields.

Discord controls the final card layout and application heading. This is a custom listening activity; Spotify-specific features such as Listen Along are not included.

## Requirements

- Windows 10 version 2004 or newer / Windows 11, x64.
- Official Apple Music for Windows, with a track loaded in its media session.
- Discord desktop, signed in to the account where you want the presence.
- Internet access to Apple's search/artwork services for covers.

The Apple Music website and iTunes are not supported by this build.

## Data and settings

Settings are stored at `%LOCALAPPDATA%\AppleMusicPresence\settings.json`. The Application ID is a public identifier, not an account token. The app never asks for your Discord password, Discord token, or Apple login.

For artwork matching, song title and artist are sent to `itunes.apple.com`; matched artwork comes from Apple's `mzstatic.com` servers. Successful results are cached in memory for 12 hours, up to 128 tracks. Discord receives the song title, artist, public cover URL, and playback start/end timestamps through its local IPC connection.

The default catalog country comes from your Windows region. If needed, quit the app and change `Country` in the settings file to a two-letter storefront code such as `PL`, `US`, or `GB`. An unmatched lookup also tries the US storefront.

Errors go to `%LOCALAPPDATA%\AppleMusicPresence\app.log`, with a small rotated backup. Routine listening history and Discord account details are not logged.

To remove the app, turn off **Start with Windows**, quit from the tray, and delete the executable. You can also delete the settings folder above.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then run:

```powershell
.\build.ps1
```

Or use an existing SDK path:

```powershell
.\build.ps1 -Dotnet 'C:\path\to\dotnet.exe'
```

The script runs checks for media filtering, the presence payload, playback clocks/seeking, settings, cover matching/cache, actual named-pipe framing/acks/errors, and playback lifecycle transitions, then publishes a self-contained executable into `artifacts\portable`.

For an opt-in local diagnostic report (contains the current track metadata):

```powershell
.\AppleMusicPresence.exe --diagnose "$env:TEMP\apple-music-presence-diagnostic.json"
```

To record ten seconds of actual clock-label change times for local troubleshooting, quit the app first and run:

```powershell
.\AppleMusicPresence.exe --clock-check "$env:TEMP\apple-music-clock-cadence.json"
```

The cadence report contains only playback-time text and update intervals. The app keeps running after the report is written.

## Implementation references

- [Microsoft: system media session manager](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Discord: IPC and SET_ACTIVITY](https://docs.discord.com/developers/topics/rpc)
- [Discord: activity image assets](https://docs.discord.com/developers/events/gateway-events#activity-object-activity-asset-image)
- [Apple: catalog search API](https://performance-partners.apple.com/search-api)

An independent personal utility; not affiliated with Apple or Discord.
