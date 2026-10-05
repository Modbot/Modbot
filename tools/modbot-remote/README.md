# modbot-remote

A scratch tool, not part of Modbot: sends one command to the **test remote** of a companion test
copy and prints the one line of JSON it answers. Made so an engineer (or an AI agent) can drive
and read the VR panels during a playtest with nobody holding the controllers.

```
dotnet run --project tools/modbot-remote -c Release -- <data-folder> <command> [args]
```

`<data-folder>` is the test copy's `MODBOT_DATA_FOLDER`. Exit code 0 when the answer says
`"ok":true`, 1 when it says `"ok":false` (with an `error`), 2 when nothing answered.

## Starting a test copy that has a remote

Both variables, for one start, from the folder with `Modbot.exe` (or `dotnet run --project
src/Modbot.Companion.App -c Release` from the repository):

```powershell
$env:MODBOT_DATA_FOLDER = "$env:LOCALAPPDATA\ModbotTest"
$env:MODBOT_DEBUG_MODE = "1"
.\Modbot.exe
```

The log says `Test remote: listening on a pipe only this Windows account can open`. Any other
start (the real copy, or a test copy without debug mode) has no remote: nothing listens.

## Commands

| Command | What it does |
| --- | --- |
| `state` | JSON of every panel: `panel` (main), `notifications`, `dashboard` (with each control's label, kind, on/value and rectangle on its texture), `desktop` |
| `press <label>` | A dashboard tab control by label (`"Overlay on"`, `Head`, `"Left wrist"`, `"Top right"`, `Joined`, `"Width +"`, `"Width -"`) through the tab's pointer path: move, down, up at its middle |
| `slide <label> <0..1>` | A dashboard slider (`Across`, `Down`, `Distance`, `Width`, `Opacity`, `"Pop-up stays"`) pressed that far along its track |
| `tap-panel <x> <y>` | The main panel clicked at a pixel of its picture by a made-up controller |
| `scroll-panel <rows> [x y]` | The thumbstick on the main panel's list (default: its middle); negative is up |
| `put-back` | Put it back in front of me |
| `event <kind> <name> [rank] [18+] [reason]` | Debug page's Send. Kinds: `joined`, `left`, `already-there`, `changed-avatar`, `flagged-join`, `problem`, `pin`, `keep-an-eye`, `message`, `ask-for-help`. Rank: `none` or a trust rank (`user`, `known-user`, ...). 18+: `18+` or `no` |
| `run [name] [rank] [18+]` | Debug page's Send a run |
| `clear-cards` | Every pop-up down |
| `grab-panel` / `move-panel <x> <y> <z>` / `release-panel` | The made-up controller takes hold of the main panel's middle, carries it to a place in front of the head (metres: right, up, back; in front is negative z), lets go. Let go, it stays in the room where it was left, as with a real controller |
| `wait <ms>` | Waits, up to 60000 |

Quote words with spaces. Examples:

```
dotnet run --project tools/modbot-remote -c Release -- "$env:LOCALAPPDATA\ModbotTest" event flagged-join "Rin Test" user 18+ Was rude
dotnet run --project tools/modbot-remote -c Release -- "$env:LOCALAPPDATA\ModbotTest" tap-panel 512 140
dotnet run --project tools/modbot-remote -c Release -- "$env:LOCALAPPDATA\ModbotTest" slide Opacity 0.5
```

## How it works

- A named pipe (a Unix socket on Linux) named after the folder, opened with `CurrentUserOnly`, so
  only the same account can connect. One line in, one line of JSON out. No TCP, no network.
- Presses go through `DashboardHost.Handle` exactly as SteamVR's mouse events do. Taps, scrolls
  and grabs go through `MadeUpHand`, which the main panel reads in place of one real controller
  (`OverlayHost.MadeUp`); everything after that is the panel's normal controller handling, and its
  grip goes through `GripHold`.
- It sends nothing to any server and never starts SteamVR. It refuses to tap a real person's row
  or card, and Place or Clear on a heads-up. Test events only make up people (`modbot-test:` ids).
- The panel and dashboard commands need SteamVR running with the panels attached; without it they
  answer `"ok":false` and say why.

## Limits

- Only the main panel takes taps, scrolls and grabs; the notification overlay is read, not touched.
- `state` lists the dashboard's controls with rectangles; the main panel's own targets are not
  listed, so pick `tap-panel` points from a `tools/vr-shot` picture.
