# Modbot.Companion.App

The Windows companion. It reads VRChat's log, sends events to the paired Modbot server, shows
the overlay and keeps the Modbot Cloud backup.

Two flows leave the client, and they are separate. Every instance event, whatever the group, goes
straight to Modbot Cloud. Each paired group's own events go to that group's paired server, at the
address it was paired with. Nothing about the Cloud backup comes from a paired server.

## Settings file

`%APPDATA%\Modbot\settings.json`. Every field is optional.

| Field | Default | What it does |
|---|---|---|
| `pairingPage` | `https://my.modbot.co/go?redir=/pair` | The page **Pair with a server** opens. HTTPS, or plain HTTP to this PC. |
| `checkForUpdates` | `true` | Whether an installed client looks for newer versions. |
| `startWithWindows` | `true` | The start-with-Windows switch on the Settings page. |
| `vrchatLogFolder` | blank | VRChat's log folder, when it is not in the usual place. |
| `voice.on` | `false` | The Voice card's **Voice on** switch. Turning it on downloads the voice once (see `Voice/VoiceDownload.cs` in Modbot.Companion). |
| `voice.joins`, `voice.leaves`, `voice.flaggedJoins` | `true` | Which events the voice says. |
| `voice.volume` | `80` | 0 to 100. |
| `voice.outputDevice` | absent | The output device by the operating system's id; absent follows the system default. |
| `cloud.endpoint` | `https://cloud.modbot.co` | Where the Modbot Cloud backup goes. |
| `cloud.disabled` | `false` | `true` turns the Modbot Cloud backup off. |

```json
{ "cloud": { "endpoint": "https://cloud.example.org", "disabled": false } }
```

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `MODBOT_COMPANION_LOG_LEVEL` | No | `Verbose` | The lowest level written to the client's own log (`%APPDATA%\Modbot\logs`): `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Verbose`. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of what the client prints to the terminal it was started from: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. The log files are always text. |
| `MODBOT_CLOUD_ENDPOINT` | No | `https://cloud.modbot.co` | Where the Modbot Cloud backup goes. |
| `MODBOT_CLOUD_DISABLED` | No | off | `1`, `true`, `yes` or `on` turns the backup off; `0`, `false`, `no` or `off` turns it on. Any other value is ignored. |

For the Cloud address and for on or off, each on its own: the environment variable wins over
`settings.json`, which wins over the default (on, to `https://cloud.modbot.co`). An address that is
not HTTPS, or plain HTTP to this PC, is ignored and the default used. All of it is read when the
client starts.

## Build settings

| Setting | Where | What it does |
|---|---|---|
| `ModbotRelease` | MSBuild (`-p:ModbotRelease=2026.9.0`) | The release version stamped on the client. The client release workflow sets it. |
