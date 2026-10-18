# Talk in Discord, Talk in VRChat

**Status:** step 1 (the engine), 2026-10-17. Step 2 (the buttons on the headset panel, the same three
as keys on the desktop, and the docs page, `docs/content/docs/companion/talk.mdx`), 2026-10-18; §7.
**Asked for:** staff who are in VRChat want to talk to each other privately in Discord without
leaving the instance, and to swap back without fumbling for either program.

---

## 1. What it does

Four actions, and a readout of where things stand. None of them needs the Companion window to be in
front, and none of them sends anything off the PC.

| Action | VRChat's microphone | Discord's microphone |
|---|---|---|
| **Talk in Discord** | muted first | then opened |
| **Talk in VRChat** | opened last | closed first |
| **Discord Mute** | untouched | one press of the Toggle Mute key |
| **Discord Deafen** | untouched | one press of the Toggle Deafen key |

**The side being closed always goes first.** Talk in Discord never has both open for a moment, and
if VRChat's microphone cannot be muted (OSC off, VRChat not answering) Discord is **not** opened:
a private conversation must not leak into a public instance because a swap half-worked.
Talk in VRChat is the other way round and is forgiving: it closes Discord first, but if that fails
(no key set) it still opens VRChat's microphone and says what failed. The reason is the same
sentence read backwards: leaving Discord open while you talk in VRChat tells your staff more than
they needed; leaving VRChat open while you talk in Discord tells strangers.

The readout (for step 2's buttons): whether the feature is on, VRChat's microphone as **muted /
open / not known yet**, Discord as **muted** and **deafened** as Modbot last set them, and the last
problem in a sentence.

## 2. VRChat: OSC

Source: VRChat's OSC documentation, "OSC as Input Controller" and "OSC Overview"
(docs.vrchat.com), read 2026-10-17. Each point below is from there unless it says otherwise.

- **Turning it on is the user's:** *"You can turn on OSC in the Action Menu under Osc > Enabled."*
  Modbot cannot do it for them.
- **Muting is `/input/Voice`.** It is a button: an int, 1 for pressed and 0 for released.
  *"In toggle mode (when "Toggle Voice" is on in your Settings) changing `/input/Voice` from 0 to 1
  will toggle the state of mute"*, and the value has to go back to 0 before the next press. With
  Toggle Voice **off** it is push-to-mute and *"0 is muted, 1 is unmuted"*, which cannot be pressed
  once and left.
- **So Modbot supports Toggle Voice on, and checks.** One "tap" is 1, a short wait, then 0. Modbot
  never trusts the tap: it waits for VRChat to say what the microphone is now (below) and compares
  it with what was wanted. A tap that left the microphone unchanged is tried once more; if it still
  has not changed the answer is *"VRChat's microphone didn't change. Turn on Toggle Voice in
  VRChat's settings."* (A push-to-mute player who asks for *muted* ends muted anyway, which is
  right; asking for *open* is what fails, and says why.)
- **Knowing the real state: `MuteSelf`.** VRChat sends its built-in avatar parameter
  `/avatar/parameters/MuteSelf` (a bool) whenever the microphone changes, to any program that has
  told it to send avatar data. Until the first one arrives the state is *not known*. Not known is
  handled by tapping, reading the answer, and tapping again if it landed on the wrong side (at most
  two taps). Muting from not-known therefore never ends open by luck.
- **No answer at all** (OSC off, or no way to be told): the tap is not repeated and the action
  fails with *"VRChat didn't answer. Turn on OSC in VRChat's Action Menu, under Osc."* A swap
  toward Discord stops there (§1).

### 2.1 Not binding port 9001: OSCQuery

VRChat's default is to send to UDP 9001. A program that takes 9001 for itself shuts out every other
OSC program (face tracking, chatbox tools). OSCQuery is VRChat's answer (2023.3.1 and later): each
program picks a free port, advertises it on the local network with mDNS, and VRChat finds it and
sends there. So Modbot:

1. picks a free UDP port and a free TCP port, both on this PC's loopback address only (nothing is
   reachable from the network);
2. advertises a service named **Modbot** (`_osc._udp` and `_oscjson._tcp`) and serves the address
   tree on the TCP port, with the paths `/avatar/change` and `/avatar/parameters/MuteSelf`. Per
   VRChat's OSCQuery page, an app whose tree includes `/avatar` is sent `/avatar/change` and the
   avatar parameters. VRChat shows the user a notice naming the app (**Modbot**) when it first
   sees it;
3. listens on that UDP port for `MuteSelf` and ignores everything else;
4. sends `/input/Voice` to VRChat's own receive port: found through VRChat's advertised service
   (`VRChat-Client-…`), falling back to 9000, VRChat's documented default. Always to the loopback
   address, never anywhere else.

Other OSC programs keep working because Modbot takes no fixed port and sends VRChat nothing but
`/input/Voice`.

### 2.2 The library

OSC messages are small and Modbot reads and writes the few it needs itself (`Talk/OscMessage.cs`:
integers, floats, bools, strings, and bundles on the way in). OSCQuery is the part not worth
writing: it is a multicast DNS responder plus a small web server whose answers have to be what
VRChat expects, and nothing here can be tried against VRChat from a build machine. So it uses
**`VRChat.OSCQuery` 0.0.6** (MIT, "VRChat Developers", the reference implementation VRChat's own
documentation points to).

- *For:* it is VRChat's own, so the names and answers are the ones VRChat looks for; MIT.
- *Against, said plainly:* the last release is August 2023. VRChat's OSCQuery has not changed since
  2023.3.1 and the repo's other OSC users still run it, but nobody is promising updates. It brings
  `MeaMod.DNS` (MIT), `Newtonsoft.Json` 13.0.1 (MIT; the advisory the repo avoided earlier was
  against versions before 13.0.1) and `Microsoft.Extensions.Logging.Abstractions`.
- *Rejected:* writing the mDNS responder ourselves (more code to keep right, no way to test it
  here); binding 9001 and skipping OSCQuery (breaks every other OSC program, which is what was
  asked not to do).

The package's sample web page and icon are not shipped (`ExcludeAssets="contentFiles"`).

## 3. Discord: its own keys, pressed for you

Discord has no way for another program to ask it to mute without a sign-in and a developer
application, and it has no OSC. What every Discord user already has is **keybinds**: Settings →
Keybinds → *Toggle Mute* and *Toggle Deafen*, which work while Discord is not in front.

- In Modbot's Settings each person **records the same two keys** once, by pressing them (the way
  the desktop overlay's shortcut is recorded). Stored as text such as `mod+shift+m` or `f13` in
  `settings.json`, in the same spelling as the window's own keys, plus one number-pad spelling
  (`num0`…`num9`) because that is a common choice for these.
- Modbot presses them with Windows' `SendInput`: key down for each modifier, key down and up for
  the key, then the modifiers up. It sends exactly the keys the person recorded and nothing else.
  It does not read the keyboard (the client's source guard still bans every call that would).
- **Pick keys no other program wants** (`F13` to `F24`, or a number-pad combination). If Discord is
  not running there is nothing to hear the press, and it goes to whichever program is in front,
  VRChat included. Modbot cannot tell whether Discord is running without listing the PC's programs,
  which the client does not do. The keys are therefore the person's choice and nothing is
  suggested for them.
- Mouse buttons and chords (`g e`) cannot be recorded: Discord's keybinds can be a mouse button,
  but pressing one of those is not a key.

### 3.1 What Modbot remembers about Discord

Discord does not say. Modbot remembers **only what its own presses did**, starting from muted: no,
deafened: no (what Discord has when it starts):

| Press | Remembered afterwards |
|---|---|
| Toggle Mute, not deafened | muted flips |
| Toggle Deafen, going deaf | deafened on, and what *muted* was is kept |
| Toggle Deafen, coming back | deafened off, muted goes back to what it was before deafening (Discord does the same) |
| Toggle Mute while deafened | deafened off and muted off (Discord's own mute button does this; **not tried here, to be checked by the user**, and one more press fixes it if wrong) |

"Microphone open" means not muted and not deafened.

- **A hand-mute in Discord puts the memory out of step.** Modbot's next press still flips Discord's
  real state, so the buttons keep working; only the *remembered* state is wrong, and Talk in
  Discord would then press the wrong way. One more press of Discord Mute puts both right again.
  Modbot does not try to detect this.
- **Nothing is remembered across a restart** of the Companion: it starts from "not muted, not
  deafened", which is what a freshly started Discord has.

### 3.2 What each action presses

- **Open Discord's microphone:** deafened → press Deafen (this also restores the earlier mute);
  then if still muted → press Mute.
- **Close it:** deafened → nothing (already closed, and the person wanted to hear Discord);
  otherwise muted → nothing; otherwise press Mute.
- A short gap (0.15 s) between two presses so Discord takes each one.

## 4. Failure cases, and what the person is told

One sentence each, in the readout and on the settings card. None of them stops anything else in
the Companion.

| Case | What happens | Says |
|---|---|---|
| Feature off | every action does nothing | "Talk in Discord or VRChat is off." |
| OSC off in VRChat, or VRChat not running | the tap is sent; no answer comes | "VRChat didn't answer. Turn on OSC in VRChat's Action Menu, under Osc." |
| Toggle Voice off in VRChat | microphone does not open | "VRChat's microphone didn't change. Turn on Toggle Voice in VRChat's settings." |
| OSCQuery could not start (port or network refused) | VRChat's microphone can still be tapped, but nothing can be read back, so Talk in Discord stops | "Modbot could not start listening for VRChat: <reason>." |
| Discord key not recorded | nothing is pressed | "Record Discord's Toggle Mute key in Settings." (or Toggle Deafen) |
| Discord not running | not detectable (§3) | nothing; the memory drifts, one press fixes it |
| Not Windows | key presses cannot be sent | "Only works on Windows." |
| A press could not be sent | the memory is not changed | "Windows would not send the key." |

## 5. Where it lives

All in `src/Modbot.Companion/Talk/`, not near `IVRChatGate`: OSC is a local conversation with the
game on the same PC, not the VRChat web API, and none of the gate's rules (rate limits, no 429
retries, `…WithHttpInfoAsync`) apply or are touched.

| File | Job |
|---|---|
| `OscMessage.cs` | read and write OSC messages (pure) |
| `VRChatOsc.cs` | the OSCQuery service, the receiver, the tap (`IVRChatMic`) |
| `DiscordState.cs` | what Modbot remembers, and what a press does to it (pure) |
| `TalkKeys.cs` | a recorded key as the keys Windows presses (pure) |
| `TalkController.cs` | the four actions and the readout, in the order of §1 (pure, over two small interfaces) |
| `TalkSettings.cs` | on/off and the two keys, the `talk` object of `settings.json`, off and empty by default |
| `Companion.App/WindowsKeyPresser.cs` | `SendInput` (`IKeyPresser`) |
| `Companion.App/MainWindow.Talk.cs`, `CompanionHost.Talk.cs` | the settings card; building and wiring the controller |

**The Companion's promises still hold, and the source guard says so:** the OSC and press code
reads nothing from disk, opens no outbound connection beyond the loopback address, reads no
keyboard, and names no program or window. **Off until switched on:** nothing listens, advertises
or presses until a person ticks the box; unticking gives the port and the advertisement back.

## 6. Tested, and not

- Written, to run in the testing pass: the OSC reader and writer (round trips, a bundle, a bad
  packet), the key reading, the Discord memory table above, the controller's order and failure
  rules over fakes (VRChat first or Discord first, abort rules, tap-again, no answer, no key), the
  `talk` settings object.
- Not testable without the programs, and checked by the user: Discord reacting to the presses,
  VRChat muting, VRChat naming Modbot in its notice, other OSC programs still working, the
  Toggle Mute while deafened row in §3.1, `OSCQuery` on loopback being found by VRChat.

## 7. The buttons (step 2)

- **The row.** On the main panel under the tabs, on every screen and also when the panel is otherwise
  idle: **Talk in Discord/VRChat**, **Discord Mute**, **Discord Deafen** (`OverlayView.Talk.cs`).
  Drawn only while the feature is on. The first button names the side being talked in (**Talking in
  Discord**, **Talking in VRChat**), or **Talk in Discord** when neither; tapping it goes to the
  other side, and from neither goes to Discord. The other two say **Muted** / **Deafened** while on.
  A failure is one line under the row. No other text.
- **Which side.** Talk in Discord is VRChat muted and Discord open; Talk in VRChat is the opposite;
  anything else (both open, both shut, VRChat not heard from) is neither (`TalkState.Side`).
  `TalkController.SwapAsync` reads the side when its turn comes, not when it was asked.
- **Hotkeys.** Three more recorded keys in the same card (`swapHotkey`, `muteHotkey`,
  `deafenHotkey` in the `talk` object), unset by default, claimed from Windows one key each
  through `RegisterHotKey` (the desktop overlay shortcut's class) only while the feature is on. This
  narrows the clips design's "exactly one keyboard combination" (§11.2) because the owner approved
  desktop hotkeys for Talk (2026-10-17); the client now asks Windows only for combinations the person
  records. It still reads no key.
  - **Only keys that are safe to claim.** A hotkey is taken from every program, so one with no Ctrl
    or Alt must be a function key (`TalkKeys.ReadHotkey`); a bare letter, digit, punctuation mark,
    space, Enter, Tab or arrow is refused with one line on the card. A key Modbot only *presses*
    for Discord may be bare.
  - **One slot.** A hotkey and a Discord key clash on the main key, whatever is held with it.
    When `Ctrl+F13` fires Ctrl is still down, so Modbot's own F13 would reach Discord as `Ctrl+F13`
    and could fire the hotkey again or miss Discord's bind. Two Discord keys, or two hotkeys, clash
    only when they are the same keys.
  - **Held modifiers.** As a hotkey fires, `TalkController.PressHotkeyAsync` first lets go of the
    hotkey's own modifiers (`IKeyPresser.ReleaseModifiers`, key-ups only), so the Discord key goes
    out as recorded. Waiting for the person to let go would need a read of the keyboard's state,
    which the client never does (`CompanionSourceGuardTests`).
  - **Recording.** While a key is being recorded on the card the three hotkeys are given back to
    Windows and claimed again after, so recording a hotkey over itself records it.
- **Wiring.** The overlay loop only says which button was tapped (`OverlayDriver.TalkAsked`); the
  host owns the controller and acts, as for Save a clip. The host hands the loop the row on each
  turn, because VRChat can change its own microphone.
