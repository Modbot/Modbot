# Modbot Companion — a map for change requests

2026-09-26 · `staging` @ 0c55ff07 · read-only survey. Nothing in the repo was changed except this file.

This is the shared vocabulary for UI and behaviour change requests on the Windows companion. Names
are quoted exactly as the code writes them. Line numbers are as of the commit above; they drift.

## How this was made

- **Read:** `CLAUDE.md`, the App README, every page under `docs/content/docs/companion/`, the specs
  listed in §7 (plus `2026-09-16-live-updates` and `2026-09-17-credits-everywhere`, which also cover
  the client), all of `src/Modbot.Companion.App/`, the library `src/Modbot.Companion/`,
  `src/Modbot.Overlay/`, the two test projects, and the release workflow.
- **Built:** `dotnet build src/Modbot.Companion.App -c Release` succeeded with 0 warnings and 0 errors.
- **Not run from source.** The app always uses `%APPDATA%\Modbot` (no setting or environment
  variable moves it), and that folder holds real pairings. A dev build would have reported to real
  servers and re-registered the link handler and start-with-Windows entry. So instead:
- **Seen on screen:** the owner's installed copy, **2026.9.3-preview.1** (built from 4cef3145,
  2026-09-19), while VRChat was running in a group instance with `MODBOT_DEBUG_MODE` on. I went
  through every sidebar page, the Add server screen, an opened Events row, the filter picker, the
  command palette, the Debug page and the desktop overlay window (both tabs). I changed no setting
  and pressed no action button. Screenshots are not kept, because they show real players' names.
  Staging has 7 companion commits the installed copy lacks (memory use, waiting for a headset,
  bringing the window forward, logging). None of them changes a label.
- **Not seen:** the tray menu, the tray notice, the monitor notification window, the headset
  panels, the overlay preview window, crash boxes, the voice or listening states in action, and
  every state that needs a fault (Stopped, Needs update and so on). Those entries come from the
  code alone and say so.

Path shorthands used below:

| Short | Path |
|---|---|
| `MW` | `src/Modbot.Companion.App/MainWindow.cs` |
| `MW.Events`, `MW.Keyboard`, … | `src/Modbot.Companion.App/MainWindow.Events.cs`, `…Keyboard.cs`, … |
| `P` | `src/Modbot.Companion.App/Program.cs` |
| `Ctl` | `src/Modbot.Companion.App/Controls.cs` |
| `State` | `src/Modbot.Companion/Presentation/CompanionAppState.cs` |
| `Set` | `src/Modbot.Companion/Presentation/CompanionSettings.cs` |
| `Lib/` | `src/Modbot.Companion/` |
| `Ov/` | `src/Modbot.Overlay/` |
| `App/` | `src/Modbot.Companion.App/` |

---

## 1. Screen inventory

In the order a moderator meets them. "Reads" means snapshot fields (`CompanionAppSnapshot`,
`State:178`); "writes" means the `settings.json` field it saves.

### 1.1 The main window frame

`MainWindow()` `MW:138-238`. `Title = "Modbot"`, `Icon = Brand.Icon()`, 900×660, minimum 720×480,
standard Windows title bar, Fluent dark theme (`P:183-184`). Pressing **X** hides the window to the
tray (`MW:295-308`) and never quits.

```
+--------------------------------------------------------------------------------------+
| [ico] Modbot                                                             [_][ ][X]   |
+-----------------------+--------------------------------------------------------------+
| [pic] -Group name     |  _warnings  (one Ui.Note per warning, coloured left edge)    |
|       and 1 more      |  ┌────────────────────────────────────────────────────────┐  |
|                       |  │ "…" refused 2 batch(es) as malformed and they were …   │  |
| [ Search      Ctrl K ]|  └────────────────────────────────────────────────────────┘  |
|                       |  _body  (the page: cards stacked, spacing 14, margin 20,     |
|  Servers           2  |          inside one ScrollViewer)                            |
|  Events          200  |  ┌────────────────────────────────────────────────────────┐  |
|  SteamVR         off  |  │ [pic] Card title                     [pill] [button]   │  |
|  Log                  |  ├────────────────────────────────────────────────────────┤  |
|  Settings             |  │ card body                                              │  |
|  Credits              |  └────────────────────────────────────────────────────────┘  |
|  (Debug)              |                                                              |
|                       |                                                              |
| [mark] Modbot         |  <- brand foot, only once something is paired                |
| • reading VRChat's log|  <- health line                                              |
+-----------------------+--------------------------------------------------------------+
   216 px                 _panelLayer lies over everything: palette, "?" sheet, backdrop
```

| Part | On-screen text | Built by | Reads | Purpose |
|---|---|---|---|---|
| Identity row, nothing paired | mark + wordmark "Modbot" + "companion" | `RenderIdentity` `MW:404-425` | `Servers.Count` | brand |
| Identity row, paired | first server's group picture (28 px) or mark, group name, "companion" or "and {n} more" | same | `Servers[0].GroupName/GroupIconUrl` | whose group this reports for |
| Search button | "Search" + key cap "Ctrl K" | `SearchButton` `MW:373-398` | – | opens the palette |
| Nav rows | "Servers" "Events" "SteamVR" "Log" "Settings" "Credits" "Debug" | `RenderNav` `MW:452-470`, `NavItem` `:482-496` | counts, overlay state, `DebugMode` | pages |
| Brand foot | mark 16 px + "Modbot" | `RenderIdentity` | – | shown once paired |
| Health line | dot + "reading VRChat's log" / "log not recognised" / "VRChat not running" | `RenderHealth` `MW:437-450` | `LogStatus` | is the log being read |
| Warnings | one note per warning, no title | `RenderWarnings` `MW:688-696` | `Warnings` (`State:481-579`) | problems, on every page |

Pages: `enum Page` `MW:21-35` = `Servers, AddServer, Events, SteamVr, Log, Settings, Credits, Debug`.
The window opens on **Servers**. `AddServer` has no nav row, so no row is lit while it shows
(seen on screen).

### 1.2 Pages and cards

| # | Page · card | On-screen name (exact) | Built by | Reads | Writes | Purpose |
|---|---|---|---|---|---|---|
| 1 | Servers, empty | card "Not reporting anywhere", body "No servers paired." | `RenderServers` `MW:803-825` | `Servers` | – | empty state |
| 1 | Servers | button "Add a server" | `MW:805-806` | – | – | go to pairing |
| 1 | Servers | one card per server, **title = group name** with group picture, 320 px wide, in a wrap panel | `ServerCard` `MW:878-906` | `ServerRow.GroupName, GroupIconUrl, State, IsPaused, Detail, Address, ManagedGroupId` | – (pause is not saved) | is this group being reported |
| 2 | Add server | button "Back to servers" + card "Pair with a server" | `RenderAddServer` `MW:831-844`, `PairingCard` `MW:950-1014` (built once in the constructor) | `PairingPage`, `LastPairing` | `pairings.json` | pair |
| 3 | Events | no card title: filter bar, then event rows | `RenderEvents` `MW.Events:165`; rows `EventRow` `MW:1021`, `Place` `MW:1071` | `Events` (last 200), `Servers`, `EventsFilters` | `eventsFilters` | everything the client handled |
| 3 | Events · opened row | fields + "JSON" box | `DetailPanel` `MW.Events:665`; `Lib/Presentation/EventRowDetail.cs` | row | – | row detail |
| 3 | Events · filter picker | floating box, watermark "Filter by" | `_picker` `MW.Events:42`, `RefreshPicker` `:524` | properties/values | `eventsFilters` | add filter chips |
| 4 | SteamVR | card "SteamVR" (pill + "Look for SteamVR now" in the header) | `RenderSteamVr` `MW:1397-1486` | `OverlayOrNone` | `overlayOn` | headset panel on/off, health |
| 4 | SteamVR, on | card "Showing" | `MW:1461-1481` | `Showing, People, RosterAge, Alert, Problem, FollowingServer, PinnedSample` | – | what the panel shows |
| 4 | SteamVR | card "Placement" | `PlacementControls` `MW:1493-1560` | `PlacementOrDefault`, `Holding` | `overlay` | where the panel sits |
| 4 | SteamVR | card "Notification overlay" (headset pop-ups) | `RenderNotificationOverlay` `MW.Overlays:36`, title `:110` | `NotifyOverlayOrNone` | `notifyOverlay` | headset pop-up panel |
| 5 | Log | card "VRChat's log" | `RenderLog` `MW:1100-1139` | `LogDetail, LogFolder, LinesRead, BehaviourLines, RecognisedEvents` | – | proof of what is read |
| 5 | Log | card "What it does not read" | `MW:1141-1153` | – | – | fixed disclosure paragraph |
| 6 | Settings | card "Settings" (one switch; hidden unless installed) | `RenderSettings` `MW:1171-1175`, `RefreshSettings` `:1207-1214` | `Startup`, `ShowStartupCard` | `startWithWindows` | start with Windows |
| 6 | Settings | card "Desktop overlay" | `DesktopOverlayCard` `MW.DesktopOverlay:154` | `DesktopOverlayOrNone` | `desktopOverlay` | window over VRChat |
| 6 | Settings | card "Notification overlay" (monitor pop-ups) | `DesktopNotifyCard` `MW.DesktopOverlay:248` | `DesktopNotifyOrDefault` | `desktopNotifyOverlay` | corner pop-ups on a monitor |
| 6 | Settings | card "Notifications" | `NotificationsCard` `MW.Notifications:120` | `NotificationsOrDefault`, `SoundProblem` | `notifications` | sounds |
| 6 | Settings | card "Tell me about" | `NotificationFiltersCard` `MW.NotificationFilters:57` | `NotificationFiltersOrDefault` | `notificationFilters` (+ `voice`) | which kinds pop up / sound / speak |
| 6 | Settings | card "Voice" | `VoiceSettingsCard` `MW:1313-1343`, `RefreshVoiceControls` `:1235-1310` | `VoiceOrNone` | `voice` | spoken announcements |
| 6 | Settings | card "Clips" | `ClipsCard` `MW.Clips:109` | `ClipsOrNone` | `clips` | keep the last few minutes |
| 6 | Settings | card "Listening" | `ListeningCard` `MW.Listening:109` | `ListeningOrNone` | `listenForPhrase` | listen for its name |
| 6 | Settings | card "VRChat log folder" | `LogFolderSettings` `MW:1350-1379` | `LogFolder`, `LogFolderConfigured` | `vrchatLogFolder` | where the log is |
| 6 | Settings | card "Restart" | `RestartCard` `MW.Notifications:175` | – | – | restart the app |
| 7 | Credits | cards "Sponsors", "Early adopters", "Contributors", each only if non-empty | `RenderCredits` `MW.Credits:26-38` | `CreditsOrNone` | – | thanks, read from Modbot Cloud. All empty = blank page (seen: only Contributors showed) |
| 8 | Debug (only `MODBOT_DEBUG_MODE=1`) | card "Overlay" | `RenderDebug` `MW:1623-1635` | – | – | preview window, attach |
| 8 | Debug | card "Sample screens" | `MW:1637-1659` | `PinnedSample` | – | pin a sample to the headset panel |

Settings page card order is fixed in `RenderSettings` `MW:1171-1190`: Settings, Desktop overlay,
Notification overlay, Notifications, Tell me about, Voice, Clips, Listening, VRChat log folder,
Restart. Seen on screen in that order.

### 1.3 Panels over the window

| Name | Built by | Purpose |
|---|---|---|
| Command palette, watermark "Search pages and actions", "Esc" cap; groups "Go to", "On this page", "Keys on this page" | `BuildPalette` `MW.Keyboard:294`, `RefreshPalette` `:415` | pages and actions by name |
| Shortcut sheet, title "Keyboard shortcuts", groups "General", "Go to", "Lists", "Filters" | `BuildSheet` `MW.Keyboard:493-558` | read-only list of keys |
| Dim backdrop | `_panelLayer` `MW.Keyboard:44` (click outside closes, `:80-84`) | – |

### 1.4 Other windows and the tray

| Name | Built by | Opens when | Reads | Purpose |
|---|---|---|---|---|
| Tray icon, tooltip "Modbot: reporting presence for your groups" | `P:2536-2558` | always, every platform | – | the app lives here |
| Tray menu "Open Modbot" | `P:2538` | right-click | – | show window (`ShowWindow` `P:2696-2711`) |
| Tray menu "Quit — stops reporting" | `P:2541` | right-click | – | `StopEverything` `P:2569-2597`, then shut down |
| Tray notice "Modbot is minimised to the tray" | `TrayNoticeWindow.cs`; text `P:1519` | window closed with X, first 3 times only | `notifications.trayNoticesShown` | tells you it is still running |
| Desktop overlay window (header strip: group icon + group name or "Not in a group instance", "Close"; tabs "Instance", "Events") | `DesktopOverlayWindow.cs`; body `OverlayView.Build` | hotkey (default Ctrl+Alt+M) or "Open" | overlay screen | the headset panel's content on a monitor |
| Monitor notification window (corner pop-ups) | `DesktopNotifyWindow.cs`; body `NotificationView.Build` | a pop-up arrives while its switch is on | `PopUps` | pop-ups on a monitor |
| "Modbot overlay" preview window ("Nothing drawn yet." / "Frame {n}, {w}×{h}") | `OverlayPreviewWindow.cs:27-77` | Debug "Show overlay window" | last headset frame | see the headset picture without a headset |
| Crash box, title "Modbot ran into a problem" or "Modbot has stopped" | `CrashGuard.cs:129-146` | an unhandled error (Windows only; at most one a minute) | – | report a failure |
| Headset main panel ("Modbot", key `moe.bin.modbot.overlay`) | `Ov/OverlayHost.cs`, `Ov/Views/OverlayView.cs:74` | "Overlay on" and a runtime attached | overlay screen | roster, events, person, alert (see §6) |
| Headset notification panel ("Modbot notifications", key `moe.bin.modbot.notifications`) | `Ov/NotificationHost.cs`, `Ov/Views/NotificationView.cs:31` | "Notification overlay on" and a runtime attached | `PopUps` | pop-ups in the headset |

---

## 2. Every control

"Sends" means something leaves the PC. Handlers are in `P` unless another file is named. "–" = none.

### 2.1 Sidebar and window

| Label | Where | Does | Persists | Sends |
|---|---|---|---|---|
| "Search" (+ "Ctrl K") | `MW:375-396` | `OpenPalette` `MW.Keyboard:254` | – | – |
| Nav rows | `MW:454-467`, `BuildNavItem` `:511-523` | `GoTo(page)` `MW:532-540` (refuses Debug unless debug mode) | – | – |
| Window X | `MW:295` | hide + `ClosedToTray` `P:1504-1520` | `notifications.trayNoticesShown` | – |

### 2.2 Servers and Add server

| Label | Where | Does | Persists | Sends |
|---|---|---|---|---|
| "Add a server" (primary) | `MW:805-806` | go to Add server | – | – |
| "Pause reporting" / "Resume reporting" | `MW:880-881` | `TogglePause` `P:2961-2970`: flips `ServerConnection.IsPaused` in memory | **nothing** — a restart resumes | stops sends to that server; new events are dropped, not queued (`Lib/Ingest/ServerConnection.cs:196`); voice goes silent while any server is paused (`P:1533`); the Cloud backup carries on |
| "Unpair" (red outline) | `MW:883-884` | `Unpair` `P:2972-3005` → `PairingCoordinator.Unpair`: deletes token, pairing and queue. **No confirmation.** | `pairings.json`, `queue\` | nothing (the server is not told) |
| "Back to servers" | `MW:833-834` | go to Servers | – | – |
| "Pair with a server" (primary) | `MW:952-964` | `OpenPairingPageAsync` `P:3052-3069` opens `pairingPage` in the browser | – | opens `https://my.modbot.co/go?redir=/pair` (default, `Set:174`) |
| Field "Pairing token", box watermark "Paste the pairing token here" (mono) | `MW:151-152, 1002` | – | – | – |
| "Use pairing token" | `MW:966-983` | `PairAsync` `P:3014` → `GET /api/version`, then `POST /api/v{n}/companion/pair` with `{code, companionVersion, platform:"windows"}` (`Lib/Pairing/HttpPairingClient.cs:59-131`) | `pairings.json` | yes, to that server |

### 2.3 Events page (`MW.Events`)

| Label | Where | Does | Persists |
|---|---|---|---|
| "Filter" + cap "F" | `:310-323` | opens the picker at the property list ("Kind", "State", "Group", "Text") | – |
| "Clear" (only with chips) | `:327-333` | removes all chips | `eventsFilters` |
| count "{n}" or "{shown} of {n}" | `:335-341` | label | – |
| chip: name, operator button "is" / "is any of" / "is not" / "is none of" (Text: "contains"), values button, "×" | `:356-393` | flip operator, edit values, remove | `eventsFilters` |
| picker operator buttons "is any of" / "is none of" | `:573-584` | set the operator — does nothing before a value is ticked (`:580`) | `eventsFilters` |
| picker value rows (✓, label, count) | `:592-634` | toggle a value | `eventsFilters` |
| "Apply" (Text property) | `:561-566` | adds a "text contains" chip | `eventsFilters` |
| event row | `:219-224` | select; open/close the detail | – |
| JSON box (read-only) | `:67-81` | copy | – |

Opened-row fields (`EventRowDetail.cs:29-46`): "When", "Server time" (this is UTC, not the server's
clock), "Kind", "Summary", "Group", "Server", "State", "Seen only", "Note". Seen on screen. The JSON
box shows curly quotes as `“…”` escapes.

Filter values: Kind = joined, already there, left, changed avatar, log stopped, seen, note; State =
sent, waiting, withheld, failed (`Lib/Presentation/EventFilters.cs:252-268`). **The kind of a row is
worked out by matching its English sentence** (`EventFilters.cs:277-303`).

### 2.4 SteamVR page

| Label | Where | Does | Persists | Sends |
|---|---|---|---|---|
| "Overlay on" | `MW:165-171`, placed `:1427` | `SetOverlayOn` `P:2057` → `_overlaySwitch.Set` | `overlayOn` | – |
| "Look for SteamVR now" (header, only when on) | `MW:1432-1433` | `AttachSteamVr` `P:2927` → try to attach now | – | – |
| Field "Fixed to": "Head" / "Left wrist" / "Right wrist" / "Room" (current = primary) | `MW:1511-1524` | `AnchorOverlay` `P:2887` | `overlay` (500 ms later, `P:2039-2047`) | – |
| "Width {0.00} m", "Opacity {0%}", "Curve {0%}": "−" slider "+" | `MW:1546-1550`, `Stepper` `:1567`, `PlacementSlider` `:1592-1613` | `PlaceOverlay` `P:2869` | `overlay` | – |
| "Put it back in front of me" | `MW:1526-1532` | default placement, keeps width/opacity/curve | `overlay` | – |
| **Notification overlay card** (`MW.Overlays`): "Notification overlay on" | `:142-148` | switch | `notifyOverlay.on` | – |
| "Where on the screen": "Top left" "Top middle" "Top right" "Bottom left" "Bottom middle" "Bottom right" | `:98, 113-126` | spot | `notifyOverlay.spot` | – |
| "Across {0.00} m", "Down {0.00} m", "Distance {0.00} m", "Width {0.00} m", "Opacity {0%}", "Pop-up stays {0} s" (steppers) | `:99-104, 135-140` | fine placement, time on screen | `notifyOverlay` | – |
| "Put it back" | `:106-108` | defaults, keeps the switch | `notifyOverlay` | – |

Info lines (not controls): tiles "attached since", "frames drawn", "last drawn"; "Showing" lines
"Screen", "People", "Roster", "Alert", "Problem", "Reading from", "Pinned sample"; "Placement" lines
"Offset", "Held in", "Picture" (1024×1024). The notification card adds a "pop-ups up" tile.

### 2.5 Settings page

| Card | Label | Where | Does | Persists | Sends |
|---|---|---|---|---|---|
| Settings | "Start Modbot Companion when my computer starts" | `MW:158-163` | `SetStartWithWindows` `P:1608` → HKCU `…\CurrentVersion\Run` value `Modbot` | `startWithWindows` | – |
| Desktop overlay | "Desktop overlay" | `MW.DesktopOverlay:63-70` | builds/tears down the window and the hotkey (`P:2093`, `2111`, `2140`) | `desktopOverlay.on` | – |
| | "Shortcut" button ("Ctrl Alt M"; "Press the keys" while recording) | `:27-30, 80-84, 141-143` | records the next key combo; disabled when off | `desktopOverlay.shortcut` | – |
| | "Opacity" slider 20–100 + number | `:32-43, 178` | window background alpha | `desktopOverlay.opacity` | – |
| | "Open" | `:48, 78, 188` | shows the window; enabled when on and not showing | – | – |
| Notification overlay (monitor) | "Notification overlay" | `:218-225` | switch | `desktopNotifyOverlay.on` | – |
| | "Where on the screen": six spot buttons | `:257-265, 272` | corner | `desktopNotifyOverlay.spot` | – |
| | "Notification stays {n} s" slider 2–30 (no number beside it) | `:197-205, 273` | time on screen | `desktopNotifyOverlay.seconds` | – |
| Notifications | "Sound on" | `MW.Notifications:54-55` | sounds on/off | `notifications.bleep` | – |
| | "Volume" slider 0–100 + number | `:57-70` | | `notifications.volume` | – |
| | "Sound file" box (mono, no watermark), "Save", "Use Modbot's sound" | `:39-41, 76-82` | own `.wav` (16 MB, first 10 s) | `notifications.sound` | – |
| | "Test": "Chime" "Alert" "Alert twice" "Urgent" "All clear" | `:37-38, 131-136, 169` | play that sound; **disabled when the voice has no output device** (`:112`) | – | – |
| Tell me about | columns "Pop-up" "Sound" "Voice" (shown upper-case); rows "Joined" "Already there" "Left" "Changed avatar" "Flagged join" "Log stopped" "Problem"; 21 unlabelled ticks | `MW.NotificationFilters:27-96` | `SetNotificationFilters` `P:781-794` | `notificationFilters` **and** `voice.joins/leaves/flaggedJoins` | – |
| Voice | "Voice on" | `MW:179, 260-265` | `SetVoice` `P:1546`; turning on can start the download | `voice.on` (500 ms later) | the 305 MB voice from github.com, once |
| | "Volume" slider 0–100 + number | `MW:183-196` | | `voice.volume` | – |
| | "Voice" list (Bella … Lewis) | `MW:198, 241-257` | | `voice.name` | – |
| | "Output device" list ("Windows default" / "System default"; an unplugged pick shows "Not connected") | `MW:197, 1245-1269` | | `voice.outputDevice` | – |
| | "Test" | `MW:127, 200` | `TestVoice` `P:1560` (can start the download too) | – | as above |
| Clips | "Keep the last few minutes, with VRChat's sound" | `MW.Clips:41-42` | `SetClips` `P:973-1019` | `clips.on` | – |
| | "Discord's sound too" | `:44-45` | | `clips.discordSound` | – |
| | "Minutes" slider 2–5 + number | `:50-60, 144` | | `clips.minutes` | – |
| | "Folder" box (watermark = current folder), "Save", "Use the usual folder" | `:28-30, 62-68, 150` | | `clips.folder` | – |
| | "Save a clip" + status word | `:31-32, 70, 122-123` | `SaveClip` `P:1034-1086`; enabled only while Recording | – | – (local file) |
| Listening | "Listen for “Modbot”" + status word | `MW.Listening:40, 119-127` | `SetListening` `P:1463`; turning on can start the 17 MB download | `listenForPhrase.on` | the phrase model, once |
| | label "MICROPHONE" + list ("Windows default" first; "Not connected") | `:34, 43-52, 79-91, 151` | | `listenForPhrase.microphone` | – |
| VRChat log folder | "Folder" box (mono; watermark = folder in use), "Save" (primary), "Use the usual folder"; line "Watching {folder}" | `MW:1350-1394` | `SetLogFolder` `P:629` | `vrchatLogFolder` | – |
| Restart | "Restart Modbot Companion" → "Restart now" (5 s to press again) → "Restarting…"; failure "Could not start another copy of Modbot." | `MW.Notifications:43-47, 198-226` | `RestartAsync` `P:2612`: opens `modbot-companion://restart` | – | – |

Settings fields with **no control anywhere**: `pairingPage`, `checkForUpdates`, `cloud.endpoint`,
`cloud.disabled`, `clips.keepGigabytes`.

### 2.6 Credits and Debug

| Label | Where | Does | Sends |
|---|---|---|---|
| Credits tile (banner/picture + name) | `MW.Credits:58-154` | opens the https link (a group's vrchat.com page wins) | opens browser |
| "Show overlay window" (primary) | `MW:1627` | opens the preview window (`P:2934`) | – |
| "Look for SteamVR now" | `MW:1630` | attach now | – |
| "idle" "roster" "flagged join" "problem" | `MW:1637-1643`, names `App/OverlaySamples.cs:29-36` | pin that sample to the headset panel (`P:2951`) | – |
| "Live" | `MW:1645-1647` | unpin; drawn **primary while a sample is pinned**, i.e. when it is *not* live | – |
| line "Pinned: {x}" / "Showing the live screen" | `MW:1656` | – | – |

### 2.7 Desktop overlay window (`App/DesktopOverlayWindow.cs`)

| Control | Where | Does |
|---|---|---|
| Header strip (icon + name) | `:137-171, 185` | drag to move (`BeginMoveDrag`, `:152-156`); position not saved |
| "Close" | `:141-142` | hide |
| Click on the panel | `:302-311` | same tap lookup as the headset → `OverlayDriver.Tap` (`P:2125`) |
| Mouse wheel | `:113-120` | scroll the roster (Instance tab only) |
| J / ↓, K / ↑ | `:322-335` | scroll the roster. Escape is ignored on purpose. |
| Tabs "Instance" / "Events" (+ the person's name while one is open) | `Ov/Views/OverlayView.cs:300-335` | switch screen |

### 2.8 Keyboard

**In the main window** — one tunnelling KeyDown handler, no Avalonia key bindings
(`MW.Keyboard:71`). Ctrl **or the Windows key** counts as `mod` (`:187`). Keys re-registered on every
sidebar draw (`RegisterWindowKeys` `:122-142`). Rules in `Lib/Presentation/Shortcuts.cs`
(`ShortcutRegistry`, chord timeout 1 s).

| Keys | Label | Group | Where | Notes |
|---|---|---|---|---|
| Ctrl K | "Search and commands" | General | `MW.Keyboard:126` | works while typing |
| ? | "Keyboard shortcuts" | General | `:127` | |
| Esc | "Close" | General | `:128`, `Escape` `:224-234` | closes panel → picker → open row → selection. When my automation sent Escape it did not close the palette or the picker (clicking outside did). Needs checking by hand; it may be the tool. |
| G then S / E / V / L / T / C | "Servers" … "Credits" | Go to | `:129-134` | |
| G then D | "Debug" | Go to | `:138` | debug mode only |
| J, ↓ / K, ↑ | "Next row" / "Previous row" | Lists | `MW.Events:131-134` | Events page |
| Enter | "Open the selected row" | Lists | `:135` | |
| F | "Add a filter" | Filters | `:136` | |
| / | "Filter by text" | Filters | `:137` | |
| Shift F | "Remove the last filter" (hidden) | Filters | `:138` | **cannot fire**: `KeyTokens.Token` drops Shift on printable keys (`Shortcuts.cs:67`), so Shift F runs "Add a filter" |

Palette "On this page" actions (`MW.Keyboard:368-409`): Servers — "Pair with a server", "Pause
reporting to {group}" / "Resume reporting to {group}"; Events — "Clear filters"; SteamVR — "Look for
SteamVR now", "Put it back in front of me"; Settings — "Test voice"; Debug — "Show overlay window",
"Look for SteamVR now". The "Keys on this page" group also lists window-wide keys like "?".

**Global hotkey** — exactly one, Win32 `RegisterHotKey` on its own thread
(`App/DesktopOverlayShortcut.cs:98-193`), default `mod+alt+m` = Ctrl+Alt+M
(`Lib/Presentation/DesktopOverlaySettings.cs:149`), registered only while "Desktop overlay" is on.
Toggles the desktop overlay window. There is no shortcut for saving a clip (by design, clips spec §11.2).

**URL scheme** — `modbot-companion://` (not `modbot://`; the csproj comment at `:58` is wrong).
Registered at every start under `HKCU\Software\Classes\modbot-companion`, "URL:Modbot pairing link"
(`App/UrlSchemeRegistration.cs:29-56`).

| Link | Handled | Does |
|---|---|---|
| `modbot-companion://pair?token=…` | `P:3091` → pipe `modbot-companion-pairing` → `HandleMessageAsync` `P:2671` | pair |
| `modbot-companion://restart` | `Main` only, `P:3096-3117` | the new copy waits up to 30 s for the old one to go |
| (pipe message `"show"`) | `P:253`, sent by a second copy started with no link | bring the window forward |

Command-line: `--autostart` (start hidden; a second autostart copy exits quietly, `P:3122`).

---

## 3. Every state and message

### 3.1 Sidebar

| State | Dot | Text | Trigger |
|---|---|---|---|
| Log `Healthy` or `Quiet` | Ok green | "reading VRChat's log" | `MW:443`. Quiet looks the same as Healthy on purpose (`:441-442`). |
| Log `NotUnderstood` | Danger red | "log not recognised" | |
| Log `Idle` | faint grey | "VRChat not running" | |
| Nav badges | faint | Servers = count, Events = count, SteamVR = "off" / "on" / nothing | `MW:454-461`. SteamVR shows nothing when on but not attached. |

### 3.2 Server card pill and sentence

Pill `StatePill` `MW:922-930`; sentence `State:618-643`; state enum `ConnectionState`
(`Lib/Ingest/ServerConnection.cs:10-31`). Faint line under it: "{address}  ·  {group id}" (`MW:892`).

| State | Pill | Colours | Sentence (exact) | Trigger | User can |
|---|---|---|---|---|---|
| `Healthy` | "Reporting" | Ok / OkDim | "Up to date. Everything observed for this group has been reported." or "{n} observations queued to send." | normal | Pause, Unpair |
| `Waiting` | "Retrying" | Info / InfoDim | "Could not reach this server on the last attempt. Waiting to retry." (+ "; {n} observations are queued and none are lost.") | network failure, 413, 429, 5xx | Pause, Unpair |
| `Paused` | "Paused" | Warn / WarnDim | "Paused. Nothing about what you do is being captured or sent to this server. Pausing stops Modbot reporting what you do from now on; it does not save it up to report later. Anything queued before you paused is still owed to this server and goes out when you resume." | "Pause reporting" | Resume, Unpair |
| `Stopped` | "Stopped" | Danger / DangerDim | "Stopped. This server rejected the device token, so nothing more will be sent and nothing is being queued for it." | 401 / 403 | Unpair only; no re-pair button |
| `NeedsRenegotiation` | "Needs update" | Danger / DangerDim | "This server has been upgraded past what this client speaks. Observations are still being queued, and will be sent once the client is updated." | 409 | Pause, Unpair; no update button, and nothing ever renegotiates (`Renegotiated` has no caller) |

Seen on screen: two "Reporting" cards with the "Up to date…" sentence.

### 3.3 Warnings above every page

`State:481-579`, drawn as `Ui.Note` with a coloured left edge (Critical = Danger, Warning = Warn,
Info = Info; `MW:787-792`).

| Trigger | Severity | Text starts |
|---|---|---|
| name heard, waiting for a command | Info | "Modbot heard its name and is listening for what to do. Nothing is recorded or sent; …" |
| microphone open | Info | "Modbot is listening for “{Called}”. Nothing is recorded or sent; the microphone closes when VRChat does." |
| log reading threw | Critical | "Reading VRChat's log failed: {fault}. Nothing is being recorded until this is fixed. …" |
| log `NotUnderstood` | Critical | "VRChat is writing the lines Modbot reads and it has recognised none of them recently. …" |
| stored pairing can't be decrypted | Critical | "The saved credential for “{id}” cannot be decrypted on this account. …" |
| stored pairing unreadable | Critical | "The saved settings for “{id}” could not be read. Pair this server again." |
| server `Stopped` | Critical | "“{id}” rejected this device. Reporting to it has stopped and will not restart on its own. …" |
| server `NeedsRenegotiation` | Warning | "“{id}” has been upgraded past what this client can speak to. Update Modbot's client; …" |
| server refused batches as malformed | Warning | "“{id}” refused {n} batch(es) as malformed and they were dropped rather than retried. That is a bug worth reporting." |
| update downloaded | Info | "Modbot {v} has been downloaded and will be installed the next time Modbot starts. …" |

**Seen live:** the malformed-batches warning was up for the owner's production server ("refused 2
batch(es)"). That is a real, current fault: two batches of events were dropped. It is outside this
map's scope, but worth looking at.

### 3.4 Pairing notices (Add server card)

Faint line always: "Opens {pairingPage} in your browser." (`MW:856`). The notice's colour follows
`PairingNoticeKind`: Working = dim, Succeeded = Ok, Failed = Danger (`MW:854-872`). It stays up for
the whole session (seen: an old "Paired with localhost:8080…" line was still showing).

| Kind | Text | Source |
|---|---|---|
| Working | "Pairing…" | `P:3019` |
| Working | "Your browser is open. Sign in there, press \"Open in Modbot\", and come back here." | `P:3063` |
| Failed | "Could not open your browser. Open {page} yourself, sign in, and press \"Open in Modbot\" — or copy the pairing token from that page and paste it below." | `P:3066-3067` |
| Failed | "Not ready yet." | `P:3017` |
| Succeeded | "Paired with {host}. Modbot will report presence for that group's instances and nothing else." | `Lib/Presentation/PairingCoordinator.cs:77-78` |
| Failed | "This pairing link has expired or was already used. …" / "{host} refused this pairing. …" / "This client and that server do not speak a common API version. …" / "{uri} did not answer like a Modbot server. …" / "Could not reach {host}. …" | `PairingCoordinator.cs:102-122` |
| Failed | token parse problems | `Lib/Pairing/PairingToken.cs:69-153` |

### 3.5 SteamVR and overlays

Headset panel pill (`MW:1413-1422`) and headset notification pill (`MW.Overlays:65-74`) use the
same rules. The runtime word comes from `P:2792-2798` (repeated at `P:2832-2838`).

| Condition | Pill | Colours | Detail sentence examples |
|---|---|---|---|
| switch off | "Off" | faint / Surface2 | – |
| attached | "Attached" | Ok | "Attached to SteamVR." / "Attached to {runtime}." |
| "refused" | "Refused" | Danger | "This build speaks FnTable:IVROverlay_028; SteamVR does not." etc. |
| "SteamVR not installed" or "not set up" | "No SteamVR" | Info | "SteamVR is not installed on this PC." / "The overlay could not be set up on this PC." |
| anything else | "Not running" | Warn | "SteamVR is not running." (seen) / "SteamVR closed." |

The words say "SteamVR" even when the runtime is OpenXR (WiVRn, Monado); only the detail sentence
names the real one. The headset notification card never shows its detail sentence.

Desktop overlay shortcut problems (red line, `DesktopOverlaySettings.cs:224-226`): "{Ctrl Alt M} is
already taken by another program." / "That shortcut cannot be used." / "Shortcuts only work on
Windows." A 5-second timeout is also reported as "already taken". There is no pill for "registered".

Monitor notification card: no states at all (no pill, no detail).

### 3.6 Voice (`MW:1299-1309`, faint text beside "Test")

| State | Text | Colour |
|---|---|---|
| no output device | "No output device on this PC" (all voice controls disabled) | faint |
| `Downloading` | "Downloading voice… {p} of {n} MB" | faint |
| `Replacing` | "Getting the new voice… {p} of {n} MB" | faint |
| `Failed` | the problem sentence, else "Voice failed" | Danger |
| `Ready` | "Voice ready" | faint |
| `NotDownloaded` | "Voice not downloaded" (seen) | faint |

Problem sentences: `App/Voice/VoiceHost.cs:272-340`.

### 3.7 Clips (`MW.Clips:176-188`, faint word beside "Save a clip")

| `ClipRecordingState` | Card word | Overlay "Save a clip" caption (`Lib/Clips/ClipButton.cs:74-89`) |
|---|---|---|
| Off | "Off" (seen) | not drawn |
| Waiting | "Waiting for VRChat" | "Waiting for VRChat" |
| NoWindow | "Waiting for VRChat's window" | same |
| NothingRecordedYet | "Nothing recorded yet" | same |
| Recording | "Recording" / "Recording. Last clip: {file}" | "Save a clip" → "Clip saved" / "Clip not saved" for 8 s |
| NotOnThisMachine | "Not available on this machine" | same |
| FolderUnusable | "The folder cannot be used" | same |
| Failed | **"Stopped"** | **"Recording stopped"** — two names for one state, against clips spec §11.3 |

Red problem line: the Linux sentence, the folder problem or the recorder's last problem
(`App/ScreenRecording.cs:278-1460`, e.g. "Windows would not hand Modbot a picture of the screen VRChat
is on."). Clips saved and room used are never shown.

### 3.8 Listening (`MW.Listening:170-181`, faint word beside the switch)

| `ListeningState` | Text |
|---|---|
| Off | "Off" (seen) |
| NotOnThisMachine | "Not available on this machine" |
| Getting | "Downloading {MB} MB — {p}%" |
| NoModel | "Not downloaded" |
| Waiting | "Waiting for VRChat" |
| NoMicrophone | "Waiting for the microphone" |
| Listening, name heard | "Listening for what to do" |
| Listening | "Listening" / "Listening. Last heard: {…}" |

Plus the two Info banners on every page (§3.3). Red line: "Listening for a phrase only works on
Windows." / "That microphone is not connected. Modbot is using the Windows default instead." / "The
microphone could not be opened. Another program may have taken it for itself, or this PC may have
none: {msg}" / "Listening stopped: {msg}" / download failures (`P:1239-1257`). The list of commands
a moderator may say is never shown.

### 3.9 Events rows (`MW:1071-1080`)

| Row | Pill | Colour |
|---|---|---|
| sent (seen) | "sent" | Ok |
| waiting (seen) | "waiting" | Info |
| withheld (paused/stopped) | "withheld" | faint |
| failed | "failed" | Danger |
| note | "note" | Warn |
| seen (no server) or Cloud-only | no pill | text dimmed |

Empty states: "Nothing yet." / "Nothing matches." (`MW.Events:193, 199`). Journal notes from this PC:
"Saved a clip of the last few minutes as {name}. It is on this PC only." (`P:1084` — **written before
the save is known to have worked**), "Heard “{said}” and saved a clip. …", "Heard “{said}”, but …"
(`P:1332-1409`), "Paused…" / "Resumed reporting." (`ServerConnection.cs:156`).

### 3.10 Log page

`State:581-593`: "VRChat is not running, so there is nothing to read." / "Reading VRChat's log: {n}
lines seen, {n} of them the kind Modbot looks at, {n} recognised." (seen) / "Reading VRChat's log.
Nothing has happened in a while: …" / "VRChat is writing the lines Modbot reads and it no longer
recognises any of them." / "Starting up." Tiles "lines seen", "lines Modbot looks at", "recognised".

### 3.11 Updates, crash, tray

| State | On screen | Source |
|---|---|---|
| update downloaded | Info warning (§3.3) | `App/Updates.cs:228` |
| checking / downloading / check failed / up to date | **nothing** (log only) | `Updates.cs:214-233` |
| `checkForUpdates` false | nothing | – |
| crash, non-fatal | box "Modbot ran into a problem": "Modbot ran into a problem {doing}. It is still running, but that part may not be working. …" | `CrashGuard.cs:129-146` |
| crash, fatal | box "Modbot has stopped" | same |
| reader failed 5 times | non-fatal box "Reading VRChat's log failed five times in a row, so the reader has been stopped. Restart the companion once the cause is fixed. …" | `P:1707-1716` |
| on Linux | no box, log only | – |
| tray | tooltip always "Modbot: reporting presence for your groups", also when paused or unpaired | `P:2551` |

### 3.12 States with no on-screen representation

| State | Where it lives | Note |
|---|---|---|
| Cloud backup on/off/sending/retrying/queued/dropped | `Lib/CloudBackup/CloudEventBackup.cs:12-26` | deliberate (cloud-backup-stays-out-of-the-way spec) |
| Live link word per server: Off / Connecting / Live / Polling / Stopped | `ServerRow.Live` `State:38, 607`; `Lib/Overlay/LiveLink.cs:110` | computed every second, drawn nowhere; the docs and live-updates spec promise it on the Servers card; removed in 401ab1db |
| Accepted / de-duplicated counts per server | `ServerRow.AcceptedTotal/DeduplicatedTotal` | removed from the card; pairing.mdx still promises three counts |
| Pause survives restart? | – | it doesn't; nothing says so |
| Events aged out of a server queue | `FileEventBuffer.Dropped` | not exposed |
| Update checking/failed | – | see §3.11 |
| Start-with-Windows turned off in Task Manager | `StartupState.TurnedOffInWindows` | switch greyed with no reason |
| Headset notification panel detail and last-drawn time | `NotifyOverlayStatus.Detail/LastDrawnAt` | not on its card |
| Saved clips and space used | `ClipsStatus.SavedClips/UsedBytes` | not shown |
| Commands the listener accepts | `ListeningStatus.Phrases` | not shown |
| Desktop overlay hotkey registered | `ShortcutState.Registered` | only failures show |
| Desktop overlay showing | `DesktopOverlayStatus.Showing` | only disables "Open" |

---

## 4. The flows

### 4.1 First launch to first paired server

1. `Main` `P:3080-3175`: Velopack hooks (`Updates.RunInstallerHooks`, `:3086`) → link in args
   (`:3091`) → restart link? (`:3096`) → `--autostart`? (`:3100`) → log (`:3102`) → crash hooks
   (`:3103`) → mutex `Local\Modbot.Companion` (`:3105`) → second copy passes its link or "show" over
   the pipe and exits (`:3119-3144`) → first copy installs a waiting update (`:3149`) → Avalonia
   (`:3156`).
2. `OnFrameworkInitializationCompleted` `P:199-233` → `CompanionHost.Start` `P:464-548`, in order:
   journal (`sent.jsonl`), settings, pairing store, one `HttpClient`, Cloud backup (`:565-601`),
   credits (`:674-702`), voice and sounds (`:704-750`), log engine (`:1633-1658`), connections for
   stored pairings (`:503-509`), pop-ups (`:515`), the four overlay switches (`:525-533`), tray
   (`:2536`), URL scheme + pipe (`:2653-2661`), updates (`:2632-2642`), start-with-Windows (`:614`),
   the 1-second refresh (`:540`).
3. Window opens on **Servers**, card "Not reporting anywhere" (`MW:817-819`).
4. "Add a server" → Add server screen → "Pair with a server" (`MW:952`) → browser at `pairingPage`.
5. On the web: sign in, pick the server, "Open in Modbot" → Windows runs
   `Modbot.exe "modbot-companion://pair?token=…"` → the second copy forwards it over the pipe
   (`P:3137-3141`) → `HandleMessageAsync` (`P:2671`) → `ShowWindow` → `PairAsync` (`P:3014`).
   (Or paste the token and press "Use pairing token".)
6. `PairingCoordinator.PairAsync` (`Lib/Presentation/PairingCoordinator.cs:50-80`) →
   `HttpPairingClient` → `pairings.json` (token DPAPI-encrypted).
7. `P:3027-3038`: `Connect` (`P:1773-1794`: `ServerClock`, `FileEventBuffer`, `ServerConnection`,
   `_engine.Add`), overlay told, notice "Paired with…". The next render shows the server card.

**Docs vs code:** pairing.mdx says press **Pair with a server** on the Servers page and paste the
token there; the code puts both on a separate screen behind **Add a server**. It says the card's
title is the server's address; the code uses the group's name and picture. It promises three counts
under the card; they were removed.

### 4.2 Someone joins an instance → overlay, voice, pop-up

1. `_engineLoop` every 1 s (`P:301, 1656`) → `EngineTickAsync` `P:1668` → `CompanionEngine.TickAsync`
   (`Lib/Pipeline/CompanionEngine.cs:135`).
2. `PresenceObserver.Poll` (`Lib/Pipeline/PresenceObserver.cs:158`) → `VRChatLogTail.ReadPending`
   (`Lib/LogReading/VRChatLogTail.cs:90`, newest `output_log_*.txt`, polled, up to 8 MiB a pass).
3. Line → `VRChatLogLineParser.TryParse` (`:37`) → tag `Behaviour` → `BehaviourEventParser.Parse`
   (`:32`): `OnPlayerJoined <name> (<id>)` → `PlayerJoinedEvent`.
4. `InstanceSessionTracker.PlayerJoined` (`Lib/Instances/InstanceSessionTracker.cs:213`) → while
   arriving, joins are held and become "already here" (`PresenceObserved`) when the burst closes;
   once present → `Joined`.
5. Fan-out (`CompanionEngine.cs:142-144`), all observations from any instance:
   - Cloud backup `Offer` → own task → `POST https://cloud.modbot.co/api/v1/events`.
   - `VoiceAnnouncer.Offer` (`Lib/Voice/VoiceAnnouncer.cs:93`) → queue → `_voiceLoop` 250 ms →
     "Rin and Kai joined your world". Silent while any server is paused.
   - `EventNotifier.Offer` (`Lib/Presentation/EventNotifier.cs:31`) → `PopUps.Show` (filtered by
     "Tell me about") + `NotificationSound.Ask`.
6. `EventRouter.DispatchAll` (`Lib/Routing/EventRouter.cs:92`): group instance whose group matches a
   paired server → `ServerConnection.Accept` (`:191`) → queue file → journal "waiting". No match → a
   "seen" row.
7. `PumpAsync` (`ServerConnection.cs:266`) sends 2 s after a join/leave (else 30 s or 50 events):
   `POST /api/v{n}/companion/events`, Bearer device token → "sent".
8. Overlay (separate read path): `_overlayLoop` 250 ms → `OverlayDriver.TickAsync`
   (`Ov/Driving/OverlayDriver.cs:357`) follows the current instance → `GET /companion/context` +
   WebSocket `/companion/ws` (long-poll fallback) → `person_joined` / `flagged_join` → roster
   re-read; a flagged join → alert card + pop-up + `AlertShown` (`P:1586-1592`) → "Alert" sound +
   voice "Flagged user {name} joined". Cards this device reported itself (`byThisDevice`) are skipped.
9. Drawn on the headset (`OverlayHost`), the desktop overlay window (`App/OverlayScreens.cs`) and
   the pop-up surfaces (`P:2478-2491`).

**Docs:** install.mdx and overlay.mdx describe this faithfully, apart from the missing live-link word.

### 4.3 Recording a clip

1. "Keep the last few minutes…" → `SetClips` `P:973-1019` → saved → `ApplyClips`.
2. Every render, `ApplyClips` (`P:811-920`) asks `ClipRecordingRule.Decide`
   (`Lib/Clips/ClipRecordingRule.cs:86`) and builds or drops a `ScreenRecording`. It runs on the UI
   thread and checks the folder (write + delete a test file) every second while Clips is on.
3. Recorder thread (`ScreenRecording.cs:416-679`): find VRChat's window, pick the screen, DXGI
   duplication, 15 fps, ≤1280 wide, H.264 + AAC, two rolling files in `%APPDATA%\Modbot\clips`.
   VRChat's sound (and optionally Discord's) by per-process loopback (`App/ClipSound.cs`).
4. Save: card button, overlay "Save a clip" (`P:1998`), or "Modbot, clip that" (`P:1335`) →
   `SaveClip` `P:1034-1086` → make room (5 GB) → name `World_instance_yyyy-MM-dd HH-mm-ss.mp4` →
   `AskToSave` → journal note (before the outcome is known).
5. Recorder moves the older rolling file into the folder (`Harvest` `:1226-1276`) → `AnswerTheSave`
   `P:932-966` (10 s limit) → "Clip saved" / "Clip not saved" for 8 s.

**Docs:** clips.mdx matches. The Settings card's own "Save a clip" button is not mentioned.

### 4.4 Pausing reporting

"Pause reporting" (`MW:880`) or the palette → `TogglePause` `P:2961` → `ServerConnection.IsPaused`
(`:156`): state Paused, journal note, new observations dropped with a "withheld" row, no sends,
no clock checks. Voice goes silent while **any** server is paused (`P:1533`). Cloud backup
continues. **Not saved** — a restart resumes reporting.

**Docs:** pairing.mdx matches, but doesn't say a restart un-pauses.

### 4.5 An update arriving

`Updates.Start` (`P:2632-2642`, only if `checkForUpdates`) → first check 30 s after start, then every
4 h (`Updates.cs:48-55`) → Velopack `SimpleWebSource` at
`https://cloud.modbot.co/api/v1/updates/companion/releases.{channel}.json` (`:252-268`) → download →
`UpdateReady` → Info warning. Installed at the next start before any window (`P:3149`); the Restart
card leads there too. No screen for checking, failing or installing.

Release side: tag `companion-v*` or manual run with a `-preview.N` version →
`.github/workflows/companion-release.yml`: build, **both test suites run**, publish, `vpk pack`
(signed only if a certificate is set), `vpk upload github --merge`; Linux job merges its files in.
Cloud re-reads GitHub every 15 min. The Linux job's copy of the version check lacks the
"manual run must be a preview" rule.

**Docs:** install.mdx and preview.mdx match.

### 4.6 A crash

`CrashGuard.Install` (`P:3103`) hooks the AppDomain (fatal) and unobserved tasks (log only);
`InstallForUi` (`P:213`) hooks the dispatcher (non-fatal). Every timer tick runs inside
`CrashGuard.Run(doing, …)`. `Report` (`CrashGuard.cs:108-148`) logs, then on Windows shows a
top-most message box, at most one non-fatal box a minute. No automatic restart. Five log-read
failures in a row stop the reader until restart (`P:1707-1716`).

**Docs:** install.mdx matches.

### 4.7 The phrase listener hears its phrase

1. Listening on and VRChat's log live → `ApplyListening` (`P:1109`) builds `PhraseListening`
   (own thread, below normal priority, WASAPI shared mode; `App/Listening/PhraseListening.cs:240`).
2. Keyword spotter hears "Modbot" / "Mod bot" → `P:1277-1283` posts to the UI → `NameHeard.Heard()`
   (5 s window) → Info banner "Modbot heard its name…".
3. Within 5 s: "clip that/this", "show overlay" or "hide overlay" → `P:1268` posts `HeardAPhrase`
   (`P:1311`) → 6 s no-repeat gap (`PhraseHeard`).
4. Save a clip (§4.3), or `ShowOrHideTheOverlay` (`P:1373-1410`, headset main panel only, never
   writes settings).
5. Answer: spoken ("Clip saved.", "Overlay shown.", "The overlay is switched off, so there was
   nothing to show." …, `P:1341-1435`) if the voice is on; otherwise the soft Chime on success and
   nothing on failure (`P:1448-1457`). Journal note "Heard “…” and …".

**Docs:** listening.mdx matches, except it says to "untick **Overlay** on the Settings page"; the
switch is "Overlay on" on the **SteamVR** page.

### 4.8 Other places docs and code disagree

| Doc | Says | Code |
|---|---|---|
| install.mdx:127, settings.mdx:6-10 | Settings page = start switch, voice, log folder (+ Tell me about, Clips, Listening) | also Desktop overlay, Notification overlay, Notifications and Restart. **Restart is documented nowhere.** |
| install.mdx | shortcut table | leaves out G then C (credits.mdx has it) |
| install.mdx | "…cannot be decrypted on this **Windows** account." | "…on this account." |
| overlay.mdx | "1 prior moderation action" | "1 prior action" |
| overlay.mdx:247-248 | Servers page shows Off / Connecting / Live / Polling / Stopped | not drawn |
| settings.mdx | spoken example names the group ("Cat Lounge…") | speaks the server's host name (`P:1577`) |
| pairing.mdx | message table | lacks "{host} refused this pairing…" |
| install.mdx file table | – | lacks `credits.json`, `phrases`, `clips` (on their own pages) |
| App README | settings and env tables | lacks `MODBOT_DEBUG_MODE` and ten settings objects (`overlayOn`, `overlay`, `notifyOverlay`, `desktopOverlay`, `desktopNotifyOverlay`, `notifications`, `notificationFilters`, `eventsFilters`, `clips`, `listenForPhrase`) |
| `App/CompanionLog.cs:15` | `client-<date>.log` | writes `companion-<date>.log` (`:87`) |

---

## 5. How the UI is built

### 5.1 The vocabulary: `Ui.*` (`Ctl`)

No XAML, no view model, no styles. Each helper makes a plain Avalonia control and sets its
properties from `Ui.T` = `DesignTokens.Desktop` (dark palette + Dense sizes).

| Helper | Makes | Notes |
|---|---|---|
| `Text(text, size?, brush?, weight, wrap, mono)` `:29` | TextBlock | default 12 px, Text colour |
| `Dim` `:46`, `Faint` `:49` | TextBlock | dim / faint 11 px |
| `Label` `:53` | TextBlock | UPPER-CASE, 11 px, letter-spaced — tile and field captions |
| `Card(child, title?, action?, picture?)` `:63` | Border | header row (picture, title, action on the right), hairline, body. Radius 10 (hard-coded) |
| `Picture(bitmap, size)` `:112` | Image in a rounded Border | |
| `Pill(text, colour, background)` `:130` | dot + text, fully rounded | all state pills |
| `Note(message, edge)` `:155` | Border with a 2 px left edge | warnings |
| `Button(caption, primary, danger)` `:165` | Button | 30 px; primary = accent fill; danger = red outline |
| `Input(watermark?)` `:188` | TextBox | 30 px |
| `Field(label, input)` `:203` | dim label above a control | |
| `Hairline` `:210`, `Kbd(keys)` `:221`, `ListRow` `:261`, `Sheet` `:284`, `Stat(label, value)` `:303` | line, key caps, list row, floating panel, number tile | Stat value 22 px mono |

There is **no helper** for CheckBox, Slider or ComboBox: they are built raw and look like Avalonia's
Fluent dark theme, with the accent colour set to the brand purple (`P:181-197`). The stepper
("−" slider "+") is `Stepper` in `MW:1567`, not in `Ui`.

`Brand` (`App/Brand.cs`): `Icon()` = `Assets/Modbot.ico` for every window and the tray;
`Mark(size)` = `Assets/icon-256.png` decoded once; `Wordmark(TextBlock)` = embedded Bricolage
Grotesque. `Assets/icon-64.png` is unused.

### 5.2 Design tokens

`Ov/DesignTokens.cs`. Colours are copies of `src/Modbot.Web/src/index.css`, plus a few tints from
`explore/design/index.html`. `DesignTokenDriftTests` fails the build if they drift.

| Token | Dark (window) | VrDark (headset) |
|---|---|---|
| Background / Surface / Surface2 / Surface3 | #0f0e15 / #17151f / #1e1b29 / #262235 | #1a1824 / #211e2c / – / – |
| Border / Border2 | #2a2738 / #3a3650 | #3a3650 / – |
| Text / TextDim / TextFaint | #e6e6eb / #a5a3b8 / #6e6c80 | #f2f2f6 / #bdbbcc / – |
| Accent / AccentForeground / AccentDim | #6d5cf0 / #ffffff / #2a2547 | same |
| Danger, Warn, Ok, Info (+ Dim) | #f0526a, #fbbf24, #4ade80, #38bdf8 | same |

| Density | Row | Control | Text base / small / tiny | Hairline | Radius | Used by |
|---|---|---|---|---|---|---|
| Dense | 34 | 30 | 13 / 12 / 11 | 1 | 6 | the window, desktop pop-ups |
| Comfortable | 44 | 36 | 14 / 13 / 12 | 1 | 6 | nothing |
| Vr | 56 | 48 | 18 / 16 / 14 | 2 | 8 | headset panels **and the desktop overlay window** |

Fonts are named ("IBM Plex Sans, Segoe UI…") but not bundled, so Segoe UI or Inter is what actually
draws. Hard-coded values outside the tokens: radius 10 for cards, sheets and tiles, sidebar 216,
server card 320, sliders 300/220, stepper buttons 48×40, the sheet shadow, and one colour in
`MW.Keyboard:46`.

### 5.3 How a page redraws ("render in place")

```
Program._refresh (DispatcherTimer, 1 s, UI thread)  ─┐
nearly every action handler calls Render() at once  ─┤
GroupPictures arrival (Dispatcher.UIThread.Post)    ─┘
      │
      ▼
Program.Render()  P:2713-2772
   refresh state (log health, overlays, live words, ApplyClips, ApplyListening,
   sound problem, voice, credits, desktop overlay)
   snapshot = _state.Snapshot()          (immutable record, State:446-479)
   Window.Render(snapshot, new MainWindowActions(27 delegates))
      │
      ▼
MainWindow.Render  MW:319-339
   same page + same pictures + snapshot.LooksTheSameAs(drawn)?  → return
   RenderIdentity (rebuilt), RenderHealth + RenderNav (in place), DrawPage
      │
      ▼
DrawPage  MW:546-556
   PageAlreadyDrawn()?  (snapshot minus the parts this page ignores, MW:571-667)
     yes → RefreshPage()   put new values into kept controls (Settings → RefreshSettings MW:1197)
     no  → RenderPage()    clear _body, build the page again, re-attach kept controls
```

- The state (`CompanionAppState`) raises no events; the window polls a snapshot every second.
- **Kept controls**: anything a person may be in the middle of using (sliders, lists, text boxes,
  checkboxes, the pairing card) is created once as a field and moved into the new container on a
  rebuild with `DetachFromParent` (`MW:1678`). Refills skip a slider under the pointer or focused,
  a list that is open, a box that has focus. `Quiet` — true for the whole of every draw and every
  refill — stops a control from echoing back as a save, and a draw asked for during another waits
  for it to finish (`MW.Draw`; render-in-place spec §4.2, added after this survey).
- **Rebuilt every time**: labels, pills, rows, chips, buttons, tiles, spot buttons.
- Ignore table (`MW:616-667`):

| Page | Ignores |
|---|---|
| Events | Log, Overlays, Settings |
| SteamVR | Log, Events, Settings, Servers |
| Log | Events, Overlays, Servers |
| Settings, Credits | Log, Events, Overlays, Settings, Servers |
| Debug | Log, Events, Settings, Servers |
| Servers, Add server (default) | Log, Events, Overlays, Settings |

`Parts.Settings` covers only Startup, Voice, Notifications, NotificationFilters, LogFolder and
LogFolderConfigured. **Clips, Listening, DesktopNotifyOverlay and SoundProblem are not in it**, so a
change in any of them rebuilds the whole Settings page. While listening is on, the microphone list
is re-read every 5 s as a new list, so the page rebuilds every 5 s and any open drop-down closes.
The Clips and Listening status texts are only set at build time, so they rely on this rebuild.

### 5.4 Threads

| Thread | What runs there |
|---|---|
| UI thread (DispatcherTimers) | window refresh 1 s; **log reading and the send loop** 1 s (`_engineLoop`); overlay loop 250 ms; controller input 33 ms; voice loop 250 ms; settings saves 500 ms; update checks. `ApplyClips`, `ApplyListening` and the microphone listing also run here. |
| Thread pool | the rest of each engine tick after its first network wait (`CompanionEngine.cs:156-158` uses `ConfigureAwait(false)`); Cloud backup loop (`P:596`); credits (`P:681`); downloads; the pairing pipe; the live WebSocket receive loop; group pictures |
| Own threads | clip recorder ("Modbot clips", below normal); clip sound (Windows audio callbacks); phrase listener; hotkey message loop; OpenXR frame loop |
| Back to the UI | `Dispatcher.UIThread.Post/InvokeAsync` at `P:1268, 1277, 2659`, `GroupPictures.cs:134`, `DesktopOverlayShortcut.cs:186` |

`CompanionAppState` has no locks; it is safe only because everything that writes it runs on the UI
thread. Two places break that rule in practice: the recorder's status properties are read across
threads with no lock (`ScreenRecording.cs:220-249`), and the engine tick walks `Connections` on a
pool thread while pairing or unpairing can change it on the UI thread. Neither has been seen to fail.

### 5.5 What a change of each kind touches

**A new card on the Settings page**
1. Kept controls as fields plus a `SetUpX()` called from the constructor (`MW:217-222`), in a new
   `MainWindow.X.cs` partial (the pattern every recent card follows).
2. `XCard()` building the body, calling `DetachFromParent` on each kept control.
3. One line in `RenderSettings` `MW:1171-1190`.
4. `RefreshXControls(snapshot)` and a call in `RefreshSettings` `MW:1216-1223`.
5. A snapshot field in `CompanionAppSnapshot` (`State:178-218`), an `…OrNone` accessor, filled in
   `Snapshot()` (`State:446-479`); a property on `CompanionAppState`.
6. Add the field to `Parts.Settings` (`MW:643-654`), or the page rebuilds whenever it changes. If
   it holds a list, teach `LooksTheSameAs` (`State:274-294`) to compare it item by item.
7. An `init` action on `MainWindowActions` (`MW:1724-1792`), wired in `P:2753-2771`, with a handler
   in `P` that updates `_state.Settings` and calls `CompanionSettings.SaveX`.
8. Settings file: property, field constant, `FileShape` property and `Load` line in `Set`, plus a
   settings record in the library if it is an object.
9. Optional: palette entries (`MW.Keyboard:368-409`), docs page, tests in
   `tests/Modbot.Companion.Tests/Presentation/`.

**A new setting (one switch)** — `Set` (property, `…Field` constant `:176-200`, `FileShape`
`:543-559`, `Load` `:234-258`; `SaveSwitch` exists), handler in `P`, action on `MainWindowActions`,
the control and refresh as above.

**A new page** — `Page` enum `MW:21-35`; `RenderNav` `MW:452-467`; `RenderPage` switch
`MW:704-730`; `PageAlreadyDrawn` switch `MW:576-585` (otherwise it takes the Servers row of the
ignore table); `RefreshPage` `MW:675-685` (otherwise it runs `RenderPairingNotice`); a `g` key in
`RegisterWindowKeys` `MW.Keyboard:128-140`; palette "Go to" list `MW.Keyboard:330-346`; docs
install.mdx page list and shortcut table.

**A new warning** — one `yield return` in `State:481-579`. Nothing in the window.

**A new server state** — `ConnectionState` (`Lib/Ingest/ServerConnection.cs:10`), `StatePill`
`MW:922`, `DetailBrush` `MW:932`, `Detail` `State:618-643`.

**A new overlay screen** — `OverlayPage` (`Ov/Views/OverlayScreen.cs:16-32`) and any new fields on
`OverlayScreen` plus `LooksTheSameAs` (`:106-126`); a builder in `Ov/Views/OverlayView.cs` and its
tab in `Tabs` (`:300-335`); new tap targets in `Ov/Interaction/OverlayTarget.cs` and their handling
in `OverlayDriver.Tap` (`Ov/Driving/OverlayDriver.cs:268-298`); the allowed-targets list in
`tests/Modbot.Overlay.Tests/Views/OverlayPageTests.cs`; a sample in `App/OverlaySamples.cs` if it
should be previewable. It shows on the desktop overlay window for free.

**A new event kind** — about a dozen places, end to end:

| Layer | File |
|---|---|
| parse | `Lib/LogReading/VRChatLogEvent.cs`, `BehaviourEventParser.cs:32` (order matters) |
| track | `Lib/Instances/InstanceSessionTracker.cs:122`, `PresenceKind` in `ObservedPresence.cs:6` |
| wire | `CompanionEventType` `Lib/Ingest/CompanionEvent.cs:7`, `PresenceEventMapper.ToWireType` `:112` (throws on unknown), `ServerConnection.IsChange` `:229` |
| journal + Events page | `SentJournal.Sentence` / `Describe` (`Lib/Journal/SentJournal.cs:391, 421`), **`EventFilters.Kinds` / `KindsOf` (`Lib/Presentation/EventFilters.cs:265-303`, matches the sentence)** |
| notify | `NotificationKind` (`Lib/Sounds/BleepRule.cs:9`), `NotificationFilters` (`:45, 138, 152, 230-251`), `Tunes.For` (`Lib/Sounds/Tune.cs:67`), `EventNotifier` card |
| voice | `AnnouncementKind`, `AnnouncementQueue.Next` (`Lib/Voice/AnnouncementQueue.cs`), `VoiceAnnouncer.Offer` |
| Cloud | `src/Modbot.Cloud/Engine/EventTypes.cs` |
| server | `src/Modbot.Api/Features/Companion/Events/EventsHandler.cs` `ToFactType` (`:322`), `FactType`, labels, analytics, live stream |
| overlay (if pushed back) | `Lib/Overlay/LiveEvents.cs:6`, `OverlayDriver.cs:488-510`, `OverlayView` event words (`:416-424`) |

---

## 6. The overlay and the desktop overlay

### 6.1 What each can show

| Surface | Screens | Source |
|---|---|---|
| Headset main panel | group line (icon + name, or "Not in a group instance"); health banner; flagged-join alert ("Flagged user joined · {group}", name, trust rank, reason, "{n} prior actions"); tabs "Instance" / "Events" / {person}; "Save a clip" bar; then **Instance** (roster: "{n} here", freshness "up to date" / "as of …" / "not loaded", "Nobody here." / "No roster loaded for this instance.", "{n} more above"), **Events** (last 10: "Joined", "Flagged join", "Left", "Already here", "Watch ended" + HH:mm; "Nothing yet."), **Person** (standing "Flagged"/"Staff"/"Member"/"Not a member" · prior actions, roles, flags, "Joined yyyy-MM-dd", "Back", "Refresh") | `Ov/Views/OverlayView.cs:74-773` |
| Headset main panel on a wrist | Wrist card: group, "{n} here", then health, or the alert, or the newest event | `OverlayView.cs:175-218` |
| Headset notification panel | up to 3 cards: heading, big line, optional detail, coloured edge (flagged red, problem amber, plain white). Nothing to tap. | `Ov/Views/NotificationView.cs:31-100` |
| Desktop overlay window | header strip (icon + name + "Close") + the **same** `OverlayView` as the headset, idle card instead of nothing | `App/DesktopOverlayWindow.cs` |
| Monitor notification window | the same `NotificationView`, at Desktop tokens, 340 px wide, in a corner | `App/DesktopNotifyWindow.cs` |

Nothing on any panel can ban, kick, warn or note. Seen on screen: the desktop overlay window with
"{group}" in the header **and again** in the panel's group line; roster rows with a coloured dot, a
trust rank and a red "1 prior action" chip; the Events tab with "Joined"/"Left" rows.

### 6.2 Input

| Surface | How |
|---|---|
| Headset | Controller ray (35° down from the controller), nearest hand wins; **trigger** taps; **grip** picks the panel up; while held, thumbstick Y = distance, X = width; let go within 0.10 m of a hand = wrist, else it stays in the room; **double grip** (500 ms) = back in front of the head; thumbstick Y over a list scrolls. Polled every 33 ms. No gaze. (`Ov/Interaction/OverlayInteraction.cs:121-290`) |
| Desktop overlay window | mouse click (same tap targets), wheel (roster), J/K/↑/↓, drag the header, "Close", global hotkey Ctrl+Alt+M |
| Voice | "Modbot, show overlay" / "Modbot, hide overlay" — main headset panel only |
| Notification panels | none (monitor one is click-through) |

### 6.3 Anchors and placement

Main panel: Head (default: 0.35 m right, 0.28 m down, 1.0 m ahead, 0.45 m wide), Left wrist, Right
wrist (0.16 m), Room. Width 0.12–1.5 m, opacity 0.1–1, curve 0–1, distance 0.3–3 m. Texture
1024×1024. Saved as `overlay`.
Notification panel: always head-fixed, six spots (default Top right), 1.0 m, 0.35 m wide, 95%,
6 s per pop-up (2–30). Texture 256×256. Saved as `notifyOverlay`.
Desktop overlay window: 520×720, right edge of the main window's screen, not saved. Monitor
pop-ups: six corners, 24 px margin.

### 6.4 Runtimes

`Ov/FallbackOverlayRuntime.cs`: try OpenVR (SteamVR) first, as a background app so SteamVR is never
launched; use OpenXR (WiVRn, Monado) only if OpenVR is absent or refuses overlay apps. States
Running / NoRuntime / NotStarted / Refused. The app polls every 250 ms and retries every 10 s. Both
panels share one session per runtime. Rendering is headless Avalonia (Skia) to a bitmap, then a
shared D3D11 texture on Windows or raw bytes elsewhere; redrawn only when the screen changes.

### 6.5 Samples

`App/OverlaySamples.cs`: "idle", "roster", "flagged join", "problem" (group "Sample group"; Rin
flagged with 2 prior actions, Kai staff, Mira member, Jo). Pinned from the Debug page onto the
headset panel only.

### 6.6 The web app's three densities

The web has Dense (default), Comfortable and Vr (`data-density`), and the C# tokens mirror all three
by value. But nothing lets a moderator choose one here: the window is always Dense, the headset
always Vr, Comfortable is unused. The desktop overlay window draws at **Vr** size because
`OverlayView` hard-codes `DesignTokens.Vr` (`OverlayView.cs:38`), while its header strip is Dense.
The drift test doesn't check tiny text, and it has drifted: web Vr 16 px, C# Vr 14 px. There is no
counterpart for the web's light theme or phone sizes.

---

## 7. Specified vs built vs documented

### 7.1 Spec status lines

| Spec | Status (quoted) |
|---|---|
| 2026-09-04 foundation | "Draft, awaiting approval" |
| 2026-09-10 m3-client-overlay | "Draft, awaiting review" |
| 2026-09-12 client-protocol | "Draft, awaiting review" |
| 2026-09-15 cloud-log-backup | "Building" |
| 2026-09-16 brand | "Applied to the landing page, the web app, my.modbot.co and Modbot Cloud" (the companion is not listed) |
| 2026-09-16 overlay-openxr-and-interaction | no Status line ("Corrected 2026-09-19", "Narrowed 2026-09-19") |
| 2026-09-16 live-updates | "Implemented with this document" |
| 2026-09-17 update-checking | "Implemented with this document" |
| 2026-09-17 credits-everywhere | "built, 2026-09-17." |
| 2026-09-18 desktop-overlay | "built" (changed 2026-09-19) |
| 2026-09-18 two-overlay-modes | "built" (widened twice 2026-09-19) |
| 2026-09-18 notifications (server) | "Implemented with this document, except where §7 says otherwise" |
| 2026-09-18 notification-sound-and-restart | "Implemented with this document" |
| 2026-09-18 voice-engine | "Implemented with this document" |
| 2026-09-19 companion-clips | "built, 2026-09-19. Changed the same day" |
| 2026-09-19 companion-notification-filters | "Implemented with this document" |
| 2026-09-19 companion-render-in-place | "Implemented with this document" |
| 2026-09-19 listening-for-a-phrase | "built, 2026-09-19, then changed twice the same evening." |
| 2026-09-19 cloud-backup-stays-out-of-the-way | "Implemented with this document" |
| 2026-09-19 people-already-in-an-instance | no Status line |
| 2026-09-19 who-is-watching-and-who-reported | no Status line (mostly server work) |

There are no companion plans in `.agent/plans/`.

### 7.2 Specified but not built

| Item | Spec |
|---|---|
| Live-link word per server on the Servers card | live-updates §173; removed in 401ab1db |
| Verbose-logging launch flags with a copy button; detecting the store build | M3 §2.3.0-2.3.3 |
| Agreeing the API version again after a 409 (`Renegotiated` has no caller) | protocol §2.1, §7 |
| Version-mismatch message naming both numbers | M3 §9.2.1 |
| Updater picking the newest *compatible* release | M3 §9.2.1 |
| README table of every file read and field sent | foundation §3.2, M3 §3.2 |
| Overlay "pairing finished" card | M3 open question 5 |
| Signing, MSI, publisher-pinned checks | M3 §9.4 (waiting on a certificate) |
| Window capture proper, Linux recording, in-memory buffer, tie to a ban | clips §10 (deferred on purpose) |
| "not age verified" / "18+" notification kinds | notification-filters §2.1 (deferred) |
| Per-server kept cards; keeping focus/scroll on rebuild; custom select | render-in-place §7 |
| Server-pushed health criticals to the headset (`ClientNotificationChannel`) | notifications §7.6 |
| Any measurement of clips, listening or voice candidates | clips §4, listening §10, voice §2 |

### 7.3 Built but not in any spec

- The Servers page redesign (group-named cards, "Add a server" screen, pills) — 401ab1db.
- The command palette, the shortcut sheet and all window keys.
- The five sound "Test" buttons (the spec says one), the "Put it back" button on the headset
  notification card, "Offset"/"Picture"/"Held in" lines, the "pop-ups up" tile.
- The Person tab captioned with the person's name.
- J/K keys in the desktop overlay window.
- Clips card words "Off", "Recording. Last clip: …", "Stopped"; the "Save" folder button.
- Listening status words; spoken answers for overlay show/hide and "The clip could not be saved.";
  journal notes for voice commands.
- The separate "Tell me about" card (the spec drew it inside "Notifications").
- UI-thread disk and device work: the clips folder test every second, microphone listing every 5 s,
  up to 5 s blocking on hotkey registration and on stopping the recorder.

### 7.4 Promised by the docs, not done by the code

- Live-link word on the Servers page (overlay.mdx:247-248).
- "recorded / already known / queued" counts under each server (pairing.mdx:111-116).
- Pairing from a button on the Servers page, card titled by address (pairing.mdx:28, 98-99).
- "untick Overlay on the Settings page" (listening.mdx:160-161).
- Everything else checked in the docs matches (labels, timings, limits, file names, shortcuts).

---

## 8. Fragility and cost

### 8.1 Where a change is cheap

- **Wording of a label, card title or button**: one string, usually in a `MainWindow.*.cs` partial.
  Check for a test first (§8.4) and for the same words in the docs.
- **Wording of a status sentence, warning or pairing message**: one string in `State` or
  `PairingCoordinator`; pinned by `CompanionAppStateTests` / `PairingCoordinatorTests`.
- **Order of cards on Settings**: `MW:1171-1190`.
- **A new warning**: one `yield return`.
- **Colours**: tokens are shared; a change in `DesignTokens.cs` must match the web CSS or the drift
  test fails.

### 8.2 Where it is expensive

- **Anything that changes when a page rebuilds.** The render-in-place rules are subtle and silent:
  forget `Parts.Settings`, `DetachFromParent`, `Quiet` or the refill guards and you get
  a closing drop-down, a slider that jumps mid-drag, or a save that echoes. Only
  `tests/Modbot.Companion.App.Tests` (the Settings page) tests the window.
- **Anything touching Program.cs** (3,176 lines): it is the composition root *and* the controller for
  every card. Each new action threads through `MainWindowActions` (27 delegates, rebuilt every
  second).
- **Event kinds** (§5.5): a dozen files across four projects, and the Events page filter works by
  matching English sentences, so a reworded sentence silently breaks filtering.
- **The overlay panel's layout**: several tests check pixels and positions (§8.4).
- **Anything with a second thread** (clips, listening, hotkey, OpenXR): they sit outside the
  UI-thread safety rule.
- **Two cards with one name**: "Notification overlay" is both the headset card on SteamVR and the
  monitor card on Settings, with different labels ("Notification overlay on" vs "Notification
  overlay", "Pop-up stays" vs "Notification stays") and different controls. Any request that names
  "the notification overlay" needs to say which.

### 8.3 Test coverage

| Area | Tests |
|---|---|
| Library (`Modbot.Companion`): log reading, sessions, ingest, pairing, journal, settings files, filters, rules for clips/voice/listening/sounds, window status sentences | `tests/Modbot.Companion.Tests`, ~88 files, ~1,050 tests |
| Overlay (`Modbot.Overlay`): driver, interaction, anchors, targets, views (structure and pixels), transparency, token drift, "no runtime" paths | `tests/Modbot.Overlay.Tests`, ~40 files, ~265 tests |
| Server side of the companion API | `tests/Modbot.Api.Tests/Features/Companion`, 60 tests |
| **The App** (`MainWindow*`, `Controls`, `Program`, desktop windows, hotkey, recorder, updates) | **none**. Only `CompanionSourceGuardTests` (30) reads its source text. |
| Real SteamVR/OpenXR attach, controllers, Vulkan | none |

### 8.4 Tests a UI change can break

- **Text:** `ClipButtonRuleTests`, `OverlayDriverClipTests` ("Save a clip", "Clip saved"…);
  `OverlayDriverTests` ("as of 20 minutes ago", "Cannot reach …"); `LogToOverlayTests`
  ("up to date"); `EventNotifierTests`; `NotificationFiltersTests`; `AnnouncementQueueTests`;
  `SentJournalTests`/`SentRuleTests`; `CompanionAppStateTests` and `CompanionWindowRenderTests`;
  `DesktopOverlaySettingsTests`; `PairingCoordinatorTests`; `ShortcutRegistryTests`.
- **Layout / pixels (headset panel):** `SaveClipControlTests` (≥200×40 px, top third);
  `OverlayTransparencyTests` (opaque pixel at (512, 80) inside the tabs); `OverlayTargetsTests`
  (cursor at 95% down); `OverlayHostInputTests` (rows under 30% across); `OverlayPageTests` (fixed
  list of tappable kinds — a new button must be added there); `OverlayViewTests`.
- **Source text:** `CompanionSourceGuardTests` — renaming `StartOverlay`/`StopOverlay`, moving
  `OverlayHost.Create` out of `Program.cs`, a "Cloud" checkbox in `MainWindow.cs`, a new outbound
  request anywhere (the count is pinned at nine), a microphone or registry call outside its one
  allowed file.

### 8.5 Likely bugs found while reading (not fixed, not run)

| # | What | Where |
|---|---|---|
| 1 | Listening crashes on microphones at some sample rates: the resample loop runs `while (position <= last)` and reads `mono[whole + 1]`, one past the end when `position` lands on `last`. At 16 kHz (many Bluetooth headsets) that happens on every buffer. Confirmed by reading. | `App/Listening/PhraseListening.cs:593-599` |
| 2 | Shift F ("Remove the last filter") can never fire; it adds a filter instead. | `MW.Events:138`, `Shortcuts.cs:67` |
| 3 | The shortcut recorder accepts Alt+F4, Alt+Space and similar; records the Windows key as Ctrl; can't record Shift+letter and just keeps waiting. | `DesktopOverlaySettings.cs:68-101`, `MW.Keyboard:187` |
| 4 | Hotkey register/release block the UI thread up to 5 s / 2 s; a timeout is reported as "already taken by another program". | `DesktopOverlayShortcut.cs:124, 154, 138` |
| 5 | The monitor pop-up window sets `WS_EX_TRANSPARENT` without `WS_EX_LAYERED`, which usually does not let clicks through. Check by hand that clicks reach VRChat. | `DesktopNotifyWindow.cs:206` |
| 6 | Picker "is none of" before ticking a value does nothing. | `MW.Events:580` |
| 7 | "Saved a clip…" is journalled before the save is known to work. | `P:1084` |
| 8 | Quit/Restart lose a voice or placement change made in the last half second. | `P:2569-2597` |
| 9 | Whole Settings page rebuilds every 5 s while listening is on; open lists close. | §5.3 |
| 10 | Pause is lost on restart, with nothing on screen saying so. | `P:2961-2970` |
| 11 | Unpair deletes the token and queued events with no confirmation. | `MW:883`, `P:2972` |
| 12 | Possible race: engine tick walks `Connections` on a pool thread while pairing changes it. | `CompanionEngine.cs:154-160` |
| 13 | An OpenXR-only runtime on Windows probably gets no picture (the D3D11 surface has no bytes to hand over). | `Ov/OpenXr/OpenXrOverlayRuntime.cs:224-235`, `Ov/Rendering/OverlaySurface.cs:126` |

### 8.6 Other observations

- **No view model.** State is an immutable snapshot polled every second; the window is a
  hand-written renderer with a hand-written diff (`LooksTheSameAs` + the ignore table). That makes
  "what does this page show" easy to read and "why did this list close" hard to find.
- **Hand-built controls next to Fluent ones.** Buttons, cards, pills and inputs are `Ui.*`;
  checkboxes, sliders and drop-downs are Fluent defaults. Seen on screen: the look differs between
  cards (the Listening card's "MICROPHONE" label is inline upper-case where every other card uses a
  label above; "Notification stays" has its slider set off to the right with no number; the log
  folder "Save" is primary while the clips folder "Save" is not; status words are faint text on
  Clips/Listening but pills on SteamVR).
- **Explaining text despite CLAUDE.md.** The pairing card, the Log page paragraph, the whole "What it
  does not read" card, the server sentences, most warnings, the tray item "Quit — stops reporting",
  the tray tooltip and several journal suffixes explain rather than label. The newer partials
  (Clips, Notifications, Listening) follow the rule and say so in comments; the older window code
  argues it is "the trust argument made visible" (`MW:41-46`). Em dashes appear in UI strings
  despite the brand spec.
- **Brand drift:** mark drawn at 22/20/16 px (brand says at least 24), card radius 10 (brand says 12),
  a sheet shadow (brand: "Nothing glows"), fonts named but not bundled.
- **"Room"** is still a label on the Placement row, meaning the physical room. CLAUDE.md retired
  "room" in favour of "instance" for VRChat's meaning, so a reader may trip on it.
- The tray tooltip always says "reporting", even when paused or unpaired.
- Stale comments: "Attach to SteamVR now" (`P:2926`), `MainWindowActions` "a small surface"
  (`MW:1693`), counts "on the Events page" (`MW:798-801`), `WindowsVoiceOutput` on recording APIs,
  `JournalEntryKind.Seen` on private instances, the log file name, the csproj URL scheme.

---

## 9. Ten questions for the owner

1. **Explaining text.** CLAUDE.md bans explanations on screen, but the pairing card, the Log page, the
   "What it does not read" card, the server sentences and the warnings all explain, and the code
   calls that deliberate ("the trust argument"). When I touch those screens, should I cut them to
   labels, leave them, or is there a list of which ones stay?
2. **Pause.** Should a pause survive a restart? And should pausing one server keep silencing the
   voice for every server, as it does now?
3. **Unpair.** Should Unpair ask first? It deletes the token and any queued events at once.
4. **The live-link word and the counts.** The docs promise the Off/Connecting/Live/Polling/Stopped
   word and the three counts on each server card; the code removed them on purpose. Which is
   right — bring them back, or fix the docs?
5. **Two "Notification overlay" cards.** Should the headset one and the monitor one keep the same
   name, and should they have the same controls (status pill, "Put it back", number beside the
   slider, "Pop-up stays" vs "Notification stays")?
6. **Where things live.** Should overlay settings (headset panel, headset pop-ups, desktop window,
   monitor pop-ups) be together on one page, or stay split between SteamVR and Settings? Is
   "SteamVR" still the right page name now that WiVRn and Monado are supported?
7. **Density.** Should the desktop overlay window draw at headset size (as now) or window size? Do
   you want a density or text-size choice anywhere in the companion?
8. **Updates and Cloud.** Should there be any on-screen update state or control (checking, failed,
   "install now", the `checkForUpdates` switch)? Is the Cloud backup staying invisible and
   switch-less on screen by decision?
9. **Debug page.** Is the Debug page (and `MODBOT_DEBUG_MODE`) for developers only, or something
   testers like you should rely on? Should it be in the README and the docs?
10. **Scope of "the UI".** Do your requests cover the headset panels and the desktop overlay window
    too, or only the main window? The panels are tested by pixels and positions, so they cost more
    to change.

### Still open from this survey

- Whether Escape really fails to close the palette and filter picker, or whether that was my
  automation (§2.8). Needs one press by hand.
- Whether clicks pass through the monitor pop-up window (§8.5 #5).
- The production server refusing batches as malformed (§3.3), seen live on 2026-09-26.
