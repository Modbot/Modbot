# Modbot — the companion window draws again only when it would look different

- **Date:** 2026-09-19
- **Status:** Implemented with this document
- **Covers:** why hovering a button in the companion made it flicker once a second; why the voice
  list closed as soon as the pointer moved onto a voice; what the window now builds again, what it
  refreshes where it stands, and how it decides
- **Changes:** nothing in an earlier spec is reversed. This narrows how often the window is built:
  the companion window design's "rebuilt from a snapshot on a timer" stays true, and the timer now
  decides whether the rebuild is worth anything.
- **Related:** the overlay's `OverlayScreen.LooksTheSameAs`, which asks the same question about a
  frame in a headset and is the pattern this follows

---

## 1. What was wrong

Two things a moderator reported, and one cause.

> Hovering on a button in Companion and moving the mouse slightly causes it to re-update the
> animation for the button, which it shouldn't do.

> Hovering to a new voice in voice settings makes the voice list go away.

`Program.cs` runs a one-second timer that calls `MainWindow.Render(snapshot, actions)`, which
called `RenderPage()`, which began:

```csharp
_body.Children.Clear();
```

Every control on the page was therefore thrown away and built again once a second, whatever had or
had not happened. A control built again is a different control, and a control that has just been
put on screen has none of the state the pointer and the keyboard had given the old one:

- **The hover.** A fresh button does not know the pointer is over it until the pointer moves, so
  the hover would light up, vanish on the next tick, and light up again on the next twitch of the
  mouse. That is the first report, exactly.
- **The open list.** The voice list and the output-device list were already kept between renders —
  `_voiceDevice` and `_voiceName` are fields — but the card around them was not. Every tick the
  Settings page took each of them out of the card it was in (`DetachFromParent`) and put it in a
  newly built one. A drop-down whose control leaves the window's tree closes, so the list died a
  second after it was opened, which reads as "hovering a voice closes the list" because a second is
  about how long it takes to move the pointer down the list. **The parent's reading of this one was
  close but not exact: the list control was never destroyed, it was detached and re-attached.** The
  fix is the same either way — the card has to stay put — but it is worth knowing that holding a
  control in a field is not enough on its own.

Everything else that holds what a person is in the middle of doing had the same problem: the
sliders (guarded by `IsPointerOver`, but only guarded, not kept attached), the pairing paste box,
the log-folder box, the events picker.

## 2. What the window does now

Two changes, and the second is the one that fixes the reports.

### 2.1 A snapshot that says nothing new is drawn by doing nothing

`CompanionAppSnapshot` is a record, so `==` would be the obvious question to ask. It is never true:
`CompanionAppState.Snapshot()` builds new lists every time it is called (`[.. Connections.Select(Describe)]`
and so on) and two lists holding the same things are never the same list.

`CompanionAppSnapshot.LooksTheSameAs` answers the question that was meant. It compares the lists
item by item, then rebuilds itself with the other snapshot's lists in it and compares the two
whole:

```csharp
return Servers.SequenceEqual(other.Servers)
    && Events.SequenceEqual(other.Events)
    && Warnings.SequenceEqual(other.Warnings)
    && SameVoice(Voice, other.Voice)
    && SameCredits(Credits, other.Credits)
    && (this with { … the other's lists … }) == other;
```

The last line is the part worth keeping. Every field that is not a list is compared without anybody
listing it, so a field added to the snapshot next month is compared too. A **list** added next month
is compared by the list it came in, which says "not the same" and draws the page again — which is
what every page did before any of this existed, so forgetting one costs a redraw and can never
leave a page saying something that is no longer true.

`SameVoice` and `SameCredits` do the same one level down, because both records hold lists and both
are handed to the window in a fresh list regularly: the voice host lists the machine's output
devices again every few seconds and hands back the same devices in a new list.

### 2.2 But that alone fixes nothing while VRChat is running

While VRChat is running, `LinesRead` and the counters beside it move every second. The snapshot
genuinely differs on every tick, so the page would be built again on every tick and both reports
would stand. Skipping only helps a client with nothing happening.

So the page is drawn again only when **that page** would come out different.

## 3. Which page cares about which part of the snapshot

`PageAlreadyDrawn()` names, per page, the parts of the snapshot it can ignore: the ones it never
shows, and the ones it puts into controls it keeps rather than building again. Those parts are
taken from the snapshot the page was drawn from, so they cannot ask for a page nobody would see a
difference in; everything else is compared by `LooksTheSameAs`.

The five groups:

| Group | What is in it | Who shows it |
|---|---|---|
| Log | the log status and detail, lines seen, lines looked at, recognised | the Log page (the sidebar's health line is refreshed where it stands) |
| Events | the events | the Events page (every other page shows only how many, in the sidebar) |
| Overlays | the headset panel, the notification panel, the desktop one | the SteamVR page; the Settings page refreshes the desktop one |
| Settings | start with Windows, the voice, the notifications, the filters, the log folder | the Settings page, all of it refreshed where it stands |
| Servers | the paired servers, the last pairing attempt, the pairing page | the Servers page |

And the pages:

| Page | Ignores | So it is built again when |
|---|---|---|
| Servers | Log, Events, Overlays, Settings | a server's counts or state change, a warning appears, a pairing is answered |
| Events | Log, Overlays, Settings | an event arrives, a filter changes, a server is renamed |
| SteamVR | Log, Events, Settings, Servers | anything about either headset panel changes |
| Log | Events, Overlays, Servers | a counter moves, which is every second while VRChat runs |
| Settings | all five | a warning appears, or the thank-you lists arrive |
| Credits | all five | the thank-you lists arrive |
| Debug | Log, Events, Settings, Servers | the pinned sample or the overlay changes |

The naming runs in the safe direction on purpose. Getting the list wrong in one direction costs a
redraw. The other direction — a page that ignores something it actually shows — is the one that
leaves a moderator reading a number that is no longer true, so the parts are named one by one and
the default is to compare.

## 4. What survives a rebuild, and what does not need to

A plain label costs nothing to build again. What costs something is anything holding what a person
is in the middle of: a pointer's hover, an open list, a drag, a caret, a selection.

**Kept and refreshed where they stand:**

- **The sidebar rows.** Built the first time each page is named; after that only the badge, the
  label's colour and the lit background are set. The sidebar is drawn on every tick, so these were
  the buttons most likely to be under a pointer.
- **The whole Settings page.** Every card is built once. What changes on it reaches the screen
  through `RefreshSettings()` — the start-with-Windows switch, the desktop overlay card, the voice
  card, the notifications card, the "Tell me about" ticks, the log folder box and the line under
  it. This is what lets the voice list stay open: the list is never detached, because the card
  holding it is never built again.
- **The Test buttons and the "Watching …" line**, which used to be built with the card and so had
  to move into the refresh with everything else.
- **The warnings**, which now have their own panel above the page rather than living in it, so one
  appearing or going does not disturb the page under it.
- Everything the file already kept: the paste box, the pairing card, the sliders, the events
  picker, the JSON box, the filter ticks.

**Left to be built again**, because nothing on them holds anything: the Log page's stat tiles, the
Credits tiles, the event rows.

**One more thing had to be watched.** Group pictures arrive after the page that wanted them was
drawn, and they change what the page would draw without changing the snapshot. `GroupPictures`
therefore counts what has landed, and the window treats a new count as a reason to draw again.

### 4.1 A card that can end up with nothing in it — added 2026-09-19

A moderator reported the Settings page with a card headed **Settings** and nothing under it: a
heading, the line beneath it, and empty space. It reads as a screen that failed to draw.

The card holds one control, the start-with-Windows switch, and only an installed copy shows that
switch (M3 §9; the portable zip never touches Windows' startup list). Hiding the switch left the
card around it.

**The rule: a card whose contents can all be hidden is hidden with them, and that decision is made
in the refresh.** Deciding it where the card is built looks simpler and is wrong here — the page is
built once and refreshed after that, and the first build can happen before the host has said whether
this copy is installed, so a card built from an empty snapshot would be hidden for the rest of the
session. The card is kept in a field like every other control on this page, and `RefreshSettings`
sets its `IsVisible` from `CompanionAppSnapshot.ShowStartupCard` on every tick, beside the switch's
own.

Every other card on the page was checked. The Clips, Listening and Notifications cards each hide a
problem line when there is no problem, and the desktop overlay card hides its own, but all four
still hold a switch, so none of them can empty out. The SteamVR page's "Showing" card is built only
when the overlay is on, which is the same answer reached a different way.

### 4.2 One draw at a time, and quiet while it runs — added 2026-09-26

Reported on 2026.9.3-preview.1: Settings, opened right after the companion started, stopped after
the Listening card. "VRChat log folder" and "Restart" were missing, and the log said the log
folder box "already has a visual parent". Going to another page and back built it fully.

**The cause, seen in the log's stack trace of 2026-09-25 and done again offscreen.** The first
time a slider is put in the window, Avalonia pulls its value up to its minimum: the desktop
overlay's opacity from 0 to 20, the notification seconds to 2, the clip minutes to 2. The slider
reports that as a change. Only the refresh kept the controls quiet, not the build, so the window
took the change for a person moving the slider and saved it. The client draws the window again
after every save, and that draw started **inside the build that was still going**. It emptied the
page, built the whole of it, and put the log folder box in its own card. The build underneath then
carried on and stopped when it tried to put that box into a second card. The two builds also took
kept controls from each other's cards along the way.

It was also a bug in the settings, not only on the screen. The first visit to Settings after a
start saved the desktop overlay's opacity as 20 and the notification overlay's time as 2 s. It also
saved the Clips card as its unfilled controls read, which **turned clips off** (the log of
2026-09-25 says "Keeping the last few minutes is off" 12 ms before the error).

**The rules now:**

- **A draw never starts inside another.** `Render` and `RenderPage` both go through `Draw`. One
  asked for while a draw is running is remembered and run after it, with the newest snapshot. It
  does not get mixed into the draw that is already running.
- **The kept controls are quiet for the whole of a draw**, not just the refresh. That includes the
  build, because moving a control into the window is what makes it change its own value. `Quiet` is
  a count rather than an on-off switch, so a refresh inside a draw cannot switch it off early.
- **Every card detaches each kept control it adds**, in the method that adds it. The log folder
  box was detached at the top of `RenderSettings` and added nine cards later, which was fine only
  as long as nothing ran in between.

`tests/Modbot.Companion.App.Tests` opens the window offscreen with the real theme and fonts. It
presses the sidebar the way a person does, and checks that the first visit writes nothing, builds
every card and shows what is saved. It also checks that a slider moved by hand is still saved
afterwards.

## 5. The custom select

There is not one yet. The Settings page's two lists are Avalonia's own `ComboBox`, styled to the
design tokens in `MainWindow.VoiceDropDown()`. If a select of our own replaces them because the
native one looks wrong on Windows, it inherits this page's rule rather than needing a rule of its
own: it is built once, it is never detached, and what it shows is put into it by
`RefreshVoiceControls`, which already refuses to touch the items or the selection while the list is
open (`IsDropDownOpen`). A select built by hand should keep that guard, whatever it calls the
"open" state.

## 6. What was decided against

- **Never redrawing, and pushing every change into a refresh.** That is the whole window rewritten
  as kept controls, every page, including the event table and the per-server cards. It would be the
  most thorough answer and it would also be the easiest way to leave a page quietly stale, because
  every value would depend on somebody having written its refresh. The window is a trust screen;
  "the number on it is old" is the failure it cannot have.
- **Listing what each page shows rather than what it ignores.** Shorter to write and wrong in the
  dangerous direction: a field nobody listed would be a field nobody redraws for.
- **Comparing the snapshot loosely — the counts but not the sentences, say.** Cheaper again, and it
  turns "is this the same" into a judgement that drifts. Item by item, then the record's own
  equality, is a rule that stays true as the record grows.
- **Making the timer slower.** A status screen that reports late is a worse screen, and a two-second
  flicker is still a flicker.

## 7. What this does not fix

- **The Servers page still builds again when a server's counts change**, which while a moderator is
  in a busy instance is every few seconds. A button under the pointer flickers then. Fixing it
  means holding each server's card — the two buttons, the four tiles, the state pill and the
  sentence — keyed by server id, which is worth doing if anybody notices it, and is a bigger change
  than these two reports need.
- **The SteamVR page likewise** rebuilds whenever either headset panel reports anything new, which
  while a panel is attached is often. Its sliders are kept and refuse to be refilled while the
  pointer is on them, which is the guard that was already there, but the card around them is built
  again.
- **What is still lost when a page is built again**: the keyboard's focus, a text selection in
  anything that is not one of the kept boxes, and how far down the page was scrolled — the page's
  scroller keeps its own place, but emptying the page shrinks what is in it to nothing and takes
  the place with it. All three are lost far less often now, because the page is built far less
  often, but a rebuild still costs them.
