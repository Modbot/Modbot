# Overlay on OpenXR, and an overlay you can hold

Date: 2026-09-16. Extends the M3 companion and overlay design (2026-09-10, §6) and its
OpenVR runtime. Read that first.

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
the two failures (a refusal over a not-running, OpenXR's over OpenVR's when both refused).
`Poll`, `Submit`, `Show` and `Hide` go to whichever is attached.

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
