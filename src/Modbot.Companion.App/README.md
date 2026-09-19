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
| `ModbotRelease` | MSBuild (`-p:ModbotRelease=2026.9.0`) | The release version stamped on the client, `YYYY.M.PATCH`, or `YYYY.M.PATCH-preview.N` for a preview. The client release workflow sets it. |
| `ModbotUpdateFeed` | MSBuild (`-p:ModbotUpdateFeed=…`) | Where an installed copy asks what the newest version is. Modbot Cloud by default; see `Updates.cs`. |

## Publishing a release

`.github/workflows/companion-release.yml` builds both platforms, runs both suites on each, packs
with Velopack, signs if a certificate is configured, and publishes one GitHub release. Everything
below is done from a browser; nothing is built on your own machine.

### A release

1. Make sure `master` has what you want to ship.
2. Tag it and push the tag:

   ```bash
   git tag companion-v2026.9.2
   git push origin companion-v2026.9.2
   ```

   Year, month with no leading zero, patch. The tag is the only place the version is written; the
   exe, the installer and the update feed all take it from there.
3. Watch **Actions → Client release**. Windows runs first and creates the release; Linux merges its
   files into the same one. Both must be green before the release is complete.
4. The release appears on the releases page as the latest, with `Modbot-win-Setup.exe`,
   `Modbot-win-Portable.zip`, `Modbot.AppImage` and the update files.

### A preview

A preview is a build for a handful of testers. It goes out on its own channel, so a tester is only
ever offered newer previews and nobody on a release is ever offered a preview.

1. Push the work to a branch — `staging`, normally. The run builds whichever branch you start it
   on, so it does not have to be on `master` yet.
2. Choose the version: the release it is heading for, plus `-preview.N`. The first preview of
   `2026.9.2` is `2026.9.2-preview.1`, the next `2026.9.2-preview.2`. A preview always sorts below
   the release of the same number, so `2026.9.2` when it comes out is newer than every preview of
   it.
3. **Actions → Client release → Run workflow**. Pick the branch, type the version, run it. A
   version with no `-preview.N` is refused here: a general release comes from a tag.

   GitHub only shows **Run workflow** once the workflow file is on the default branch, so this
   workflow has to have reached `master` at least once. The branch you then pick can be any
   branch.
4. Watch both jobs, as above. The run makes the tag `companion-v2026.9.2-preview.1` itself, on the
   commit it built.
5. The release appears marked **Pre-release**, so the releases page keeps its "Latest" badge on the
   newest real release. The files are `Modbot-win-preview-Setup.exe`,
   `Modbot-win-preview-Portable.zip` and `Modbot-linux-preview.AppImage`.
6. **Before sending it to anybody**, install it on a clean Windows machine with Defender at its
   defaults and SmartScreen on, downloaded over HTTPS the way a tester would (M3 spec §8.5). An
   unsigned build trips SmartScreen and that is expected; if Defender quarantines it instead,
   submit it as a false positive and wait, rather than telling moderators to turn Defender off.
7. Send testers the installer link and a link to
   [Preview builds](https://docs.modbot.co/companion/preview), which tells them what Windows will
   say and how to go back.
8. When the preview run is over, tell testers to go back to the normal client. They are not moved
   automatically, by design: a preview only ever follows previews.

### If an installed client stops finding updates

Clients ask Modbot Cloud, which reads the GitHub releases and serves the same index
(`src/Modbot.Cloud/Features/Updates`). Check in this order:

- `https://cloud.modbot.co/api/v1/updates/companion/releases.win.json` — the index released clients
  read. `releases.linux.json`, `releases.win-preview.json` and `releases.linux-preview.json` are the
  other three.
- That the release the file should have come from is published rather than a draft, and that a
  preview is marked pre-release and a release is not. Cloud takes a pre-release's index only for
  channels whose name ends in `-preview`, and never lets one become the version deployments are
  told about.
- Cloud re-reads GitHub every 15 minutes, so a release is not expected to show up instantly.
