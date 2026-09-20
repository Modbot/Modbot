# Overlay on OpenXR, and an overlay you can hold

Date: 2026-09-16. Extends the M3 companion and overlay design (2026-09-10, §6) and its
OpenVR runtime. Read that first.

**Corrected 2026-09-19.** §4.1 said both runtimes report a hand's "aim pose", and on OpenVR that
was not true: the OpenVR path reported the raw device pose under that name and thresholded the
grip as a button. Both were wrong in a headset. §6 says what the two faults were, what replaced
them and how sure the numbers are.

**Narrowed 2026-09-19.** §4.3's "the nearer hand whose ray lands on the panel is the pointer" is no
longer both hands: the hand a panel is worn on is left out of pointing, tapping, scrolling and
grabbing it. §6.3 says why and how a panel still leaves a wrist.

## 1. Why

The overlay was written against OpenVR, which on Windows means SteamVR. On Linux a growing share
of headsets run through **WiVRn** (a standalone headset streamed to a PC) or **Monado**, both
OpenXR runtimes, with **xrizer** translating OpenVR games onto them. xrizer's own FAQ says it
runs standard OpenVR games only; overlay, utility and background applications are outside its
scope by design, and it answers an overlay with `VRInitError_Init_InvalidApplicationType`
(130). So on a WiVRn machine the companion's overlay can never attach, however correct the
OpenVR code is.

The way overlays work on Monado and WiVRn is OpenXR with the `XR_EXTX_overlay` extension:
a second OpenXR session whose composition layers the runtime draws over the main
application's. Desktop overlays on Linux (WayVR, formerly wlx-overlay-s) work this way. The
companion needs the same.

Separately, the panel is fixed in front of the head and cannot be touched. A moderator cannot
move it out of the way, put it on a wrist, leave it on a wall, dismiss an alert or open a
person's row. XSOverlay and OVR Toolkit set the expectation: grab with a controller, move,
resize, lock to head, hand or world, point and click. Section 4 designs that, for both
runtimes at once, so input is built once.

## 2. What stays the same

- Avalonia renders the panel to pixels offscreen (`AvaloniaFrameRenderer`); the compositor
  draws only on change; the drive loop reads one server's roster and alerts. None of that
  knows which runtime shows the picture.
- `IOverlayRuntime` stays the seam: `Start`, `Poll`, `Submit`, `Show`, `Hide`, `Status`.
  The OpenXR runtime is another implementation of it, and the companion picks one at start.
- Nothing here makes a network request or reads anything new on the machine. Input comes from
  the VR runtime, about the moderator's own controllers, and goes nowhere.

## 3. The OpenXR runtime

### 3.1 Choosing a runtime

`OverlayHost.Create` builds a **fallback runtime** holding both. `Start` tries OpenVR first,
because on a machine with SteamVR (Windows, or SteamVR for Linux) that is the right answer
and SteamVR's own OpenXR has no overlay extension. It goes on to OpenXR when OpenVR reports
**no runtime** (nothing installed) or **refused with InvalidApplicationType** (xrizer). Any
other OpenVR answer, including "not running", is final for that attempt; the ten-second look
tries again from the top. Status is the running runtime's, or otherwise the more telling of
the two failures: the one that can still change. A not-running over a refusal, because the
companion's ten-second look goes on only while something is **not started**, and the one
refusal that can meet a not-running here is xrizer's, which is by design and permanent (so a
WiVRn machine whose WiVRn is not up yet keeps being looked at, and attaches when it is). A
refusal over a not-installed. OpenXR's over OpenVR's when both refused. `Poll`, `Submit`,
`Show` and `Hide` go to whichever is attached.

### 3.2 Session

Bindings: **Silk.NET.OpenXR** and **Silk.NET.Vulkan** (MIT), pinned in
`Directory.Packages.props`. Silk.NET.OpenXR binds `SessionCreateInfoOverlayEXTX` and
`EventDataMainSessionVisibilityChangedEXTX`. Nothing native is shipped: the OpenXR loader
(`libopenxr_loader.so.1`) and Vulkan come from the machine, which any WiVRn or Monado user
already has; the loader name must resolve to the versioned file, since the unversioned
symlink comes only with a -dev package. A missing loader is **no runtime**, not a crash.

Order of work in `Start`:

1. `xrCreateInstance` with `XR_EXTX_overlay` and `XR_KHR_vulkan_enable2`. Application name
   `Modbot`. `XR_ERROR_RUNTIME_UNAVAILABLE`, `XR_ERROR_RUNTIME_FAILURE` or a loader that finds
   no runtime → **not started** ("No OpenXR runtime is running."); extension missing →
   **refused** ("This OpenXR runtime has no overlay extension."). The runtime's name from
   `xrGetInstanceProperties` goes into every status detail ("Attached to WiVRn.").
2. `xrGetSystem` for `HEAD_MOUNTED_DISPLAY`; Vulkan instance and device through
   `xrCreateVulkanInstanceKHR` / `xrCreateVulkanDeviceKHR`, queue from the runtime's
   requirements.
3. `xrCreateSession` with `GraphicsBindingVulkanKHR`, chaining `SessionCreateInfoOverlayEXTX`
   (`createFlags` 0, as the spec requires; `sessionLayersPlacement` 0).
4. Reference spaces: `VIEW` (head-locked, the only one §3 uses) and `LOCAL` (for §4's world
   lock). Action set for §4, attached before the session is begun.
5. One swapchain, square, `OverlayHost.DefaultResolution` a side, `B8G8R8A8_SRGB` when the
   runtime offers it, else `R8G8B8A8_SRGB` with the channels swapped on upload
   (`RawPixels.BgraToRgba`). Usage colour attachment plus transfer destination.

### 3.3 Frame loop

OpenXR has no "set texture once" as OpenVR does: a layer is submitted every frame. A
**frame thread** owned by the runtime runs `xrPollEvent`, then, while the session is
running, `xrWaitFrame` → `xrBeginFrame` → acquire and wait the swapchain image → if a new
picture is pending, copy it from a staging buffer with one command buffer and the layout
transitions → release → `xrEndFrame` with one `CompositionLayerQuad`:
`BLEND_TEXTURE_SOURCE_ALPHA` (the pixels are premultiplied, so no `UNPREMULTIPLIED_ALPHA`),
pose and size from the placement (§4.2; until then the OpenVR placement, 0.35 m right,
0.28 m down, 1.0 m ahead in `VIEW`, 0.45 m wide, square), the first environment blend mode
the runtime lists. A frame with nothing pending resubmits the image already there; the copy
is the only work that depends on content, and it happens only when the compositor drew.
`Hide` submits no layers; `Show` submits the quad again.

`Submit(surface)` from the UI thread copies `surface.Pixels` into the pending buffer under a
lock and returns; the frame thread picks it up. On Linux the surface is
`MemoryOverlaySurface`, so `Pixels` is always there. A Direct3D surface is never paired with
this runtime.

Session state: `READY` → `xrBeginSession` (`PRIMARY_STEREO`); `STOPPING` → `xrEndSession`;
`EXITING` or `LOSS_PENDING`, or an instance loss → tear everything down and report
**not started** ("WiVRn closed."), and the companion's ten-second look attaches again.
`EventDataMainSessionVisibilityChangedEXTX` is logged; the quad is shown either way,
because the panel is for the moderator inside VRChat and VRChat is the main session.

### 3.4 Status and the window

`OverlayRuntimeStatus.Detail` names the runtime, so the SteamVR page reads "Attached to
WiVRn." or "Attached to SteamVR." with no other change. `Init_InvalidApplicationType` is
already worded as xrizer's refusal; with the fallback in place it is only shown when OpenXR
also failed, and then the OpenXR detail is the one shown.

### 3.5 Testing

No test starts a real runtime. What is tested: the fallback's choice for every pair of OpenVR
and OpenXR answers; the format choice and the channel swap; the pending-buffer handoff
(a Submit before Start is kept and uploaded on the first frame; two Submits before a frame
upload once); the placement maths shared with §4. The real run is on a Linux machine with
WiVRn, from source, and the log lines say what happened at each step of §3.2.

## 4. An overlay you can hold

### 4.1 Input, once

Both runtimes report the same thing each `Poll`: for each hand, whether it is tracked, its
**aim pose** (position and forward direction) in the **same space the panel is placed in**,
and its buttons: **grab** (grip), **click** (trigger), **scroll** (thumbstick or touchpad,
two axes). OpenVR: `GetDeviceToAbsoluteTrackingPose` and `GetControllerState` through
`IVRSystem`, with the pose converted into the panel's anchor space. OpenXR: an action set
`modbot` with `aim` (pose), `grab`, `click` (boolean) and `scroll` (vector2), suggested
bindings for the Valve Index, Oculus Touch, Vive and simple controller profiles, synced each
frame and read on `Poll`. The struct is `OverlayHands` in Modbot.Overlay; the rest of §4 works
on it and never sees a runtime.

### 4.2 Placement

`OverlayPlacement` is a value: **anchor** (`Head`, `LeftHand`, `RightHand`, `World`), an
**offset** pose from that anchor, **width** in metres, **opacity**, and **curve** (0 flat).
The default is today's head placement. It is saved in `settings.json` under `overlay` and
loaded at start, so the panel is where it was left. `IOverlayRuntime.Place(placement)`
applies it: OpenVR uses `SetOverlayTransformTrackedDeviceRelative` (head, either controller)
or `SetOverlayTransformAbsolute` (world), `SetOverlayWidthInMeters`, `SetOverlayAlpha`,
`SetOverlayCurvature`; OpenXR picks the space (`VIEW`, the hand's aim action space, `LOCAL`)
and the quad's pose and size, and uses the cylinder layer when curved.

### 4.3 Holding it

`OverlayInteraction` in Modbot.Overlay takes `OverlayHands` each poll and the current
placement, and decides:

- **Pointing.** The ray from a hand's aim pose meets the panel's plane (or cylinder) at a
  point, converted to panel pixels. A pointed-at panel shows a **cursor** drawn into the
  frame (a small ring); the companion draws its own, because SteamVR draws lasers only for
  dashboard overlays and OpenXR draws none.
- **Grab.** Grip held while pointing takes the panel: it becomes anchored to that hand at its
  current offset, and follows the hand. Releasing grip leaves it where it is, anchored to
  the **world** (if it was head- or world-anchored before) or to the **hand** (if the grab
  ended within 10 cm of the wrist). A quick grip while pointing at nothing is nothing.
- **Resize and distance.** While held, scroll up and down pushes and pulls along the ray;
  scroll left and right changes the width. Bounds: 0.2–1.5 m wide, 0.3–3 m away.
- **Head lock.** Double-tap grab on the panel returns it to the head anchor at the default
  offset, so a lost panel can always be found.
- **Click.** Trigger while pointing acts on what is under the cursor.

Every change goes through `Place` and is saved. Nothing here needs a text box (M3 §6.3).

### 4.4 Tapping inside the panel

The frame is an Avalonia tree, so the cursor is a pointer over that tree. `OverlayView`
marks its targets: the alert card's **Dismiss**, each roster row, and a **scroll** region
for a long roster. A click is hit-tested against the tree that drew the current frame
(`Control.InputHitTest` on the laid-out root), and the hit target's action runs:

- **Dismiss** clears the alert (`OverlayDriver.Dismiss` already exists).
- A **row** opens that person's card: their name, standing, prior actions and flags from
  the roster already held, and their moderation history from the companion's existing
  `GET user/{subjectId}` read. The card closes on the next click outside it.
- **Scroll** moves the roster by the scroll axis while pointing at it.

Actions on people (a kick, a classification) are **not** in this design. The companion
observes and reports and never acts (M3 §10); a one-tap classification (foundation §5.8.2)
would be the server acting on the moderator's behalf and needs its own design, with the
endpoint, the permission and the audit line. The card is the step before it.

### 4.5 The window

The SteamVR page gains the placement: anchor, width, opacity, curve, and **Put it back in
front of me**. The debug page's samples keep working; a pinned sample is pointed at and
clicked like a live one.

### 4.6 Testing

`OverlayInteraction` is pure: hands in, placement and actions out, with tests for each rule
in §4.3 (grab and release to world, release near the wrist to hand, resize bounds, double
tap). Hit testing is tested through the real view on the offscreen Avalonia host, as the
rendering tests already are. Runtime input reading is checked live, on SteamVR with the null
driver plus a simulated controller where possible, and on WiVRn.

## 5. Order

1. §3, the OpenXR runtime, with `Place` taking today's fixed placement. Lands first because
   without it nothing at all shows on WiVRn.
2. §4.1–4.3 for both runtimes, with settings and the window.
3. §4.4.

## 6. Two faults a headset found (2026-09-19)

§4.1 promised that both runtimes report the same three things per hand — tracked, an **aim pose**,
and buttons. OpenXR kept that promise. OpenVR never did, in two separate ways, and a moderator
wearing the headset reported both: *"the SteamVR overlay requires a lot of grip force to grab and
move around, and the cursor points really weirdly towards the overlay rather than just me pointing
straight at it."*

Both faults are on the OpenVR path only. The OpenXR path asks for `/input/aim/pose` by name, and
its grab is a boolean the runtime thresholds itself.

### 6.1 The grab: a button where a number was wanted

**What was wrong.** OpenVR read grab as `k_EButton_Grip` out of `ulButtonPressed`. That bit is not
a switch on the controller; it is SteamVR's own decision, and on a Valve Index it is made from the
grip *force* sensor at a threshold set for picking up a heavy thing in a game. Taking hold of a
panel is not picking up a rock. The panel needed a squeeze nobody wants to make to move a window.

**What it is now.** The squeeze is read as a number — axis 2 of `VRControllerState_t`, which is
where SteamVR's own legacy bindings put an analogue grip — and `GripHold` decides:

- **0.25** of full travel takes the panel,
- it is let go below **0.15**,
- and the grip button still counts on its own, whatever the number says.

**Why those two.** A quarter of travel is a deliberate hold and is clear of what a controller reads
while it is merely sitting in a hand, which is near zero; it is also well under any threshold
SteamVR would have used for the button, so nothing that used to grab has stopped grabbing. The gap
below it — a tenth of full travel — is what stops a hand resting on the line taking and dropping
the panel thirty times a second. Without the gap a single threshold flickers, and a flickering
panel is worse than a stiff one.

The button still counting is not a fallback for tidiness. A Vive wand's grip is a switch with
nothing analogue behind it and reads zero however hard it is held; on those controllers the button
is the only answer there is. On the controllers that do have a number, the button only ever turns
on above this threshold anyway, so keeping it costs nothing.

**It is not a setting.** A number between nought and one for how hard to squeeze is not something a
moderator can judge from a settings page, and putting it there would mean explaining the grip force
sensor on a screen. The fallback to the button covers the controllers with nothing to read, and the
gap covers a wavering hand; what is left is one constant, in one place, that a later report can
move.

**OpenXR's Index binding was the same mistake, and has moved.** §4.1's bindings put the Index's
grab on `/input/squeeze/force`, reasoning that `/input/squeeze/value` reads as held whenever the
controller is simply in the hand. That was the wrong worry — the resting reading is near zero — and
the cost was real: thresholding the force sensor is exactly what makes SteamVR's grip button need a
hard squeeze. The Index now takes `/input/squeeze/value` like every other controller.

### 6.2 The pointing: a ray fired down the controller's body

**What was wrong.** `GetDeviceToAbsoluteTrackingPose` gives one pose per device, and it runs along
the controller's own body — the line the handle makes through the fist. The overlay fired the
cursor's ray straight down it. Nobody points along that line: a controller is held raked back, so a
ray along the handle leaves the hand climbing and the cursor lands above whatever is being aimed
at. The further the panel, the wider the miss. That is the skew in the report.

This is what OpenXR has two poses for. `grip` runs along the controller; `aim` is tilted off it so
that pointing works, and every runtime supplies both. OpenVR has no equivalent to ask for — its one
nod to the problem is a "tip" part inside each controller's render model, reached through an
interface Modbot does not open and whose function table would have to be indexed by position with
nothing at run time to catch a wrong guess. Modbot does not open it.

**What it is now.** `HandState` carries two poses instead of one: `Device`, where the controller is,
and `Aim`, where it points. The OpenVR path fills `Device` from the tracked pose and works `Aim` out
of it by tilting **35° downwards about the controller's own side-to-side axis**. Nothing else
moves; the ray still starts where the controller is.

**How sure that number is: not very.** 35° is the middle of the range runtimes use between the two
poses for the controllers a moderator is likely to be wearing — a Valve Index or an Oculus Touch,
whose handles are well raked. A Vive wand is a straight rod and wants far less, perhaps 5°, so a
wand now reads about 30° low; wands are the rarer controller and the complaint came from an Index.
**Nobody has checked this against a headset.** It is one named constant, `ControllerPointing.
TiltDegrees`, in one file, for exactly that reason: if it is wrong it is wrong by a number, not by
a design, and the sign is pinned by a test so a correction can only ever be a magnitude.

**The two poses are not interchangeable, and which is used where matters.** A panel worn on a hand
hangs off `Device`, because `Device` is what both runtimes attach it to: OpenVR's
`SetOverlayTransformTrackedDeviceRelative` takes the tracked device, and OpenXR's hand space is now
the grip space rather than the aim space. The ray comes out of `Aim`. Holding the panel measures
against `Device` too, so a panel carried to a hand ends up where it was carried rather than a
tilt away from it. Before there was one pose these were the same object and the question could not
be got wrong; now it can, so it is written down.

### 6.3 The hand wearing the panel does not touch it

A third report from the same headset, once §6.1 and §6.2 had made the panel movable and the cursor
land where it was aimed:

> *"if overlay is docked to left/right hand, that hand's hitbox gets in the way of moving it around
> and etc and if you grip while it's on that hand it registers you as grabbing the overlay. It
> kinda needs to ignore the left hand if it's on the left hand and vice versa."*

**What was wrong.** §4.3 said "the nearer hand whose ray lands on the panel is the pointer", and
meant it: both hands were always candidates. That is right for a panel in front of the head or left
in the room, and wrong for one worn on a wrist, because a worn panel sits *where that hand is*. Its
own ray lands on it more or less permanently, so the cursor parks itself there and will not leave,
and the hand is in the way of the other hand trying to reach past it. Worse, every squeeze of that
grip took hold of the panel — a panel already travelling with that hand, so the grab achieved
nothing except to tear it off its own wrist and leave it floating. A moderator squeezes their grip
all day for reasons that have nothing to do with an overlay.

**The rule.** The hand a panel is worn on is left out of pointing, tapping, scrolling and grabbing.
`LeftHand` ignores the left, `RightHand` ignores the right, `Head` and `World` ignore neither.

It is read off the **anchor**, which is a setting, and not off any live gesture, so it is steady:
the same hand is ignored for as long as the panel is on it, rather than coming and going with how
the hands happen to be held.

**One place, not five.** Pointing, tapping, scrolling, grabbing and the double grip that sends the
panel home are all reached through *being the pointer* — none of them is read from a hand that is
not pointing at the panel. So the rule lives in `Point`, which is the single gate, and there is no
second copy to get wrong later. `OverlayInteraction.Ignoring` says which hand, and is the whole of
it.

**Except while it is being carried.** Taking hold of the panel anchors it to the hand carrying it,
so for the length of a carry the anchor names the very hand that would otherwise be ignored. That
hand has to go on being read or the carry could never be ended and the panel would be stuck to it
for ever. Carrying is the one thing the anchored hand is allowed to do, so `Ignoring` is null while
anything is held, and comes back the moment the panel is let go.

**How a panel leaves a wrist.** The other hand points at it and grips, exactly as it would at a
panel anywhere else; two quick grips from that other hand send it back in front of the head. Off
the controllers entirely, the Placement card moves it with no headset gesture at all. What is
deliberately *not* a way off is the worn hand itself — that is the whole point — and the one case
with no way off through a controller is a panel worn on a hand whose controller is switched off,
because then there is nothing tracked for the panel to hang from and nothing to point at. The
settings page is the answer there, and is why the placement settings stay reachable with the
overlay switched off (two overlay modes design §6.1).

**Nothing is left stuck.** Two pieces of memory could have gone stale and neither does:

- The **squeeze** itself is worked out below all of this, one `GripHold` per hand in the OpenVR
  reader, from that hand's reading on that poll. An ignored hand is still read and still tracked
  truthfully; it is only the decision above that skips it. So a hand cannot come out of being
  ignored believing it is mid-grab.
- The **previous poll's buttons** are remembered for every hand, ignored or not, and that is
  deliberate. A grab is the moment a grip closes, not the fact that it is closed. A hand wearing
  the panel is very often mid-squeeze at the moment the panel moves off it, and remembering that
  squeeze is what stops the panel leaping straight back into a hand that never asked for it. That
  hand takes the panel on its next fresh squeeze, like any other.
