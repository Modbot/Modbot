# Two overlay modes — design

**Date:** 2026-09-18
**Status:** built
**Supersedes nothing.** Narrows *overlay OpenXR and interaction design* (2026-09-16) §4.2, which
assumed one panel per process.

---

## 1. What a moderator gets

There were two different jobs inside one panel, and they wanted opposite things.

A moderator in an instance needs to be **told** when a flagged person walks in, without looking
anywhere. That wants a small thing pinned to a corner of their view, always there, never in the
way, gone again in a few seconds.

The same moderator, when they decide to look, wants to **read and move around**: the roster, what
just happened, one person's card. That wants a big panel they can put on a hand or leave floating
in the room, and point a controller at.

One panel could not be both. Head-locked in a corner is useless to read a roster on; a big panel
parked in the room cannot tell you anything because you are not looking at it.

So there are two now:

| | Notification overlay | Main overlay |
|---|---|---|
| Fixed to | the head, always | head, left hand, right hand, or the room |
| Where | one of six screen positions, plus a fine offset | wherever it was left |
| Shows | short-lived pop-ups | Instance, Events, Person screens |
| Pointed at | never | yes — cursor, taps, grab |
| On by default | yes | yes |
| Settings | `notifyOverlay` | `overlay` and `overlayOn` |

Each is switched on and off on its own, and each keeps its own placement. Turning the main overlay
off leaves the notification overlay working, which is the point: mid-instance, the pop-up is the
part a moderator actually needs.

## 2. The notification overlay

### 2.1 It is not interactable

No cursor is drawn on it, no tap is tested against it, and it is never grabbed. That is not a
limitation, it is the feature: a thing fixed in the corner of your eye that also swallows trigger
presses would make the controller feel broken.

It is a separate host — `NotificationHost`, not an `OverlayHost` with a flag — and the difference
is that it has no interaction at all: no `OverlayInteraction`, no hit testing, no cursor, and
nothing in its view tree carrying an `OverlayTarget`. There is no switch to get wrong, because
there is nothing there to switch off.

### 2.2 Where on the screen

A named spot, plus a fine offset, plus a distance. The named spot is one of six — top-left,
top-middle, top-right, bottom-left, bottom-middle, bottom-right — and it is turned into a
head-relative offset in metres:

```
x = across(spot) × 0.40 × distance  +  fine across
y = down(spot)   × 0.26 × distance  −  fine down
z = −distance
```

where `across(spot)` is −1, 0 or +1 and `down(spot)` is +1 for a top spot and −1 for a bottom one.
The two fractions are the panel's angle from straight ahead — about 22° across and 15° down at any
distance — so moving the panel further away keeps it in the same place in the view rather than
sliding towards the middle. Nothing is hardcoded about the headset's field of view, because every
headset has a different one; these are comfortable angles, not edges.

That offset, with the width and opacity, becomes an ordinary `OverlayPlacement` anchored to the
head. Which means the whole existing runtime — OpenVR transforms, OpenXR spaces, clamping,
saving — works on the notification overlay unchanged. There is no second placement system.

### 2.3 Pop-ups

A pop-up is one line of news that is worth looking up for. Two things qualify today:

- a flagged person arriving,
- a Modbot fault — a rejected device token, or a server that cannot be reached while what is on
  screen is already stale.

They stack, newest at the top, at most three at once, and each clears itself after the number of
seconds in settings. `PopUps` holds them; it is told the clock like everything else in the client,
and it is the one place that decides when a pop-up has had its time. The same id twice restarts
that one's time instead of stacking a second card, which is what stops a reconnecting link from
replaying its way into a wall of pop-ups.

The list is deliberately short. The alert card on the main panel and the pop-up here are raised by
the same decision in the same place, so an alert the loop dropped — not this instance, or the same
person again within the cooldown — is not put up here either. One decision, two surfaces.

There is no pop-up for an ordinary join or leave. Those are the Events screen's job, and a pop-up
for each of them in a busy instance is exactly how an overlay earns itself being switched off.

The notification overlay draws nothing at all when the stack is empty — not a frame with nothing
in it, no frame. An always-on panel that is usually blank is the thing that makes people turn
overlays off.

### 2.4 It is still on when the main overlay is off

Both switches are independent, and each builds or tears down only its own half.

## 3. The main overlay's screens

Three, moved between by a row of tabs across the top of the panel:

- **Instance** — the roster, as before. Tapping a row opens that person.
- **Events** — what the live link has heard for this instance, newest first: joins, leaves,
  flagged joins, a watch ending. Read from what the driver already drains; no new endpoint.
- **Person** — the card that used to sit above the roster. It now has the panel to itself, with a
  **Back** control.

`OverlayScreen` carries `Page`, and `OverlayTarget.GoTo` is what a tab press means. Tapping a
roster row still opens a person, and now also moves to the Person page; **Back** returns to
Instance.

### 3.1 What the Person screen does *not* have

No ban, kick, warn or note. This is unchanged and deliberate, and it is worth writing down again
because "take actions" was asked for:

The client's device token is **ingest-scoped**. It can report presence and it can make three
reads. It could not carry a moderation action even if a button here tried, and the server would
refuse it. Adding a moderation endpoint reachable by that token would hand every copy of the
client — running unattended on personal PCs — the ability to ban, which is the exact power the
pairing model is built to withhold. `CompanionSourceGuardTests` is the fence around that promise.

So the actions on the Person screen are the ones the client honestly has: **Back**, and
**Refresh**, which re-reads that one person's summary through `IOverlayReadClient.GetUserAsync`.
A moderator acting on what they have seen does it in the web UI, signed in as themselves.

## 4. The runtime restructuring

### 4.1 OpenVR: one attachment, two overlays

`VR_InitInternal` and `VR_ShutdownInternal` are process-wide. Two `OpenVrOverlayRuntime`
instances each calling them would mean the second's init landing on top of the first's, and the
first's `Dispose` pulling the function table out from under the second.

So the attachment moved out into **`OpenVrSession`**, which owns:

- the background-application knock and the overlay init,
- the `IVROverlay` function table,
- the `IVRSystem` used for head and controller poses,
- a count of how many overlays are using it, and a **generation** number.

`OpenVrOverlayRuntime` now owns only what is genuinely per-overlay: its key, its name, its
handle, its placement, and its RGBA scratch buffer. Its `Start` opens or joins the session; its
`Dispose` destroys its own handle and lets the session go, and SteamVR is shut down when the last
overlay lets go.

The generation number is what makes a closing SteamVR safe. When one overlay reads
`VREvent_Quit`, it closes the whole session — the function table is gone for everybody. The other
overlay's next `Poll` sees the generation has moved and drops its handle **without** calling
`DestroyOverlay`, because that call would go through a function table that no longer exists.

The keys are pinned:

- main: `moe.bin.modbot.overlay` — unchanged, because SteamVR keys the moderator's own position
  and curvature adjustments on it and changing it discards them,
- notification: `moe.bin.modbot.notifications`.

The notification overlay is given a higher sort order, so when the two do overlap the pop-up is
the one on top.

### 4.2 OpenXR: one session, two layers

The OpenXR path composites both panels into its existing session, which is what the shape of
OpenXR wanted anyway. Opening a second overlay session would mean a second `XrInstance`, a second
Vulkan device and a second frame thread, for two quads that the runtime is perfectly happy to take
on one frame.

So `Attachment` now holds two **panels**, each with its own swapchain, images, uploader, waiting
picture, placement and visibility; `xrEndFrame` submits one layer per panel that has something to
show. `OpenXrOverlayRuntime` still is the main panel's `IOverlayRuntime`, and
`NotificationPanel` is a second `IOverlayRuntime` view onto the same session. The two views count
themselves in and out: the session is attached when the first one starts and let go when the last
one is disposed, so either overlay can be switched off without taking the other down.

### 4.3 Putting them together

`FallbackOverlayRuntime.CreateFor(kind, …)` builds one overlay: that kind's
`OpenVrOverlayRuntime` over the shared `OpenVrSession`, and that kind's view onto the shared
`OpenXrOverlayRuntime` (`OpenXrOverlayRuntime.Shared`, made on the first ask, for the same
one-process-one-session reason). Each host then owns its own surface and its own renderer.

One overlay per call, rather than a pair, because the two switches are independent: a moderator
with the main panel off must not have a 1024×1024 texture allocated for it, which is the rule
`OverlaySwitch` has always held — off means nothing is built.

The notification host draws at a quarter of the main host's resolution. A pop-up is three lines;
1024×1024 for it would be four megabytes of texture to say one name.

### 4.4 One drive loop, two panels

`OverlayDriver` — the loop that polls a roster, holds the live link open and turns a flagged
arrival into something on screen — is not the main panel's. It feeds both, so it runs whenever
*either* overlay is on, and stops when the last one goes.

With the main panel off, its screen is handed to `NoPanel`, which does nothing with it. That costs
one virtual call per tick and keeps the loop's own rule — draw only on change — untouched, rather
than teaching every path in it about a panel that might not be there.

This does widen one promise slightly, and the guard test now says so: the client's two outbound
overlay callers, `HttpOverlayReadClient` and `LiveSocket`, used to be off whenever the overlay was
off, and are now off whenever *both* overlays are off. That is what a moderator asking to be told
about flagged arrivals has asked for — being told requires asking somebody.

## 5. Settings

One new key, `notifyOverlay`, beside the existing `overlay` object:

```json
"notifyOverlay": {
  "on": true,
  "spot": "topright",
  "across": 0,
  "down": 0,
  "distance": 1.0,
  "width": 0.35,
  "opacity": 0.95,
  "seconds": 6
}
```

- `on` — whether the notification overlay is drawn at all. Off means nothing is built for it, the
  same rule `overlayOn` already follows.
- `spot` — one of `topleft`, `topmiddle`, `topright`, `bottomleft`, `bottommiddle`, `bottomright`.
- `across`, `down` — the fine offset in metres, on top of the spot.
- `distance` — metres ahead of the head.
- `width` — metres across.
- `opacity` — 0.1 to 1.
- `seconds` — how long one pop-up stays, 2 to 30.

Written whole by `CompanionSettings.SaveNotifyOverlay`, which rewrites the `notifyOverlay` object
and leaves every other field in the file exactly as it was — the same rule `SaveOverlay` and
`SaveVoice` follow. Anything missing or out of range takes the default for that field, so a
hand-edited file cannot put the panel somewhere it cannot be found.

`overlayOn` and `overlay` are untouched and still mean the main overlay.

**Why `on` lives inside the object here, when `overlayOn` lives outside `overlay`.** The main
overlay's object is rewritten every time a controller moves the panel, thirty times a second while
it is being dragged, and the switch must not be losable in one of those writes. The notification
overlay is never moved by a controller — it is only ever changed from the settings page, one
change at a time — so there is no write it could be lost in.

## 6. The settings page

A new **Notification overlay** card on the SteamVR page: the switch, the six spots as a row of
buttons, the fine offset, distance, width and opacity as steppers, and how long a pop-up stays.
It lives in its own partial file, `MainWindow.Overlays.cs`.

### 6.1 Switching an overlay off no longer hides its settings

Until now, switching the main overlay off replaced the whole page with the switch. That was
wrong: a moderator setting up for the first time wants to arrange where the panel will sit and
*then* turn it on, and the way to do that was to turn it on, arrange it, and turn it off again.

So the placement settings stay on the page, visible and editable, whichever way the switch is set,
and they keep saving to `settings.json` as they always did — which is why they can be edited with
nothing running: the settings are the truth, and the panel is placed from them when it comes up.
What goes away while an overlay is off is only what there is genuinely nothing to say about: how
long it has been attached, how many frames it has drawn, what it is showing, and the button that
looks for SteamVR now.

Both overlays follow this rule.

## 7. What was decided against

**A second OpenVR init.** Tempting because it keeps the two runtimes independent, and wrong: the
OpenVR entry points are process-wide, and two inits race each other's shutdown.

**A second OpenXR session for the notification panel.** Correct in the abstract — two real
overlays — and far too expensive: a second `XrInstance`, Vulkan device, swapchain set and frame
thread, running on a machine that is also running VRChat.

**Moderation buttons on the Person screen.** See §3.1. The token cannot carry them and should not
be able to.

**Queueing pop-ups that could not be shown.** A pop-up is about a moment. One delivered later
interrupts a moderator with news from twenty minutes ago, about an instance that has since
emptied. The same rule the alert card already follows.

**A `notifyOverlayOn` field beside `overlayOn`.** See §5.

**Drawing the notification overlay blank when there is nothing to say.** An always-on panel that
is usually empty is what makes people turn overlays off. Empty means no frame.
