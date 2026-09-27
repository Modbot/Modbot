# Overlay list filters design

- **Date:** 2026-09-26
- **Status:** Built on `staging`. Not yet tried in a headset: SteamVR's keyboard in particular has
  only been compiled against Valve's header, not seen working.
- **Covers:** the filter row on the overlay's Instance list and Audit Log (headset panel and desktop
  overlay window), join times on the Instance list, and typing a name in a headset
- **Builds on:** the two overlay modes design (`2026-09-18-two-overlay-modes-design.md`) and people
  already in an instance (`2026-09-19-people-already-in-an-instance-design.md`)

---

## 1. What it is

A row of filters between the tabs and the list, on both lists the panel has:

| Instance list | Audit Log |
|---|---|
| Who: All / Flagged / Members / Staff / Not in group | the same |
| Rank: any of Visitor, New User, User, Known User, Trusted User, Nuisance, Not known | the same |
| Joined: last 5 min / 15 min / hour / earlier | When: the same windows, measured from the row's time |
| Name | the same |
| Sort: standing (as before) / newest / name | Kind: any of Joined, Left, Already here, Flagged join, Watch ended |

A filter names itself until something is picked, then says what is ("Rank: New User +1"). Tapping
it opens its choices in a strip under the row; tapping it again closes them. A one-choice filter
closes once a choice is taken, so the list is back in view; Rank and Kind take several and stay
open. "Clear" appears while anything is picked. While a filter hides anyone the list says so:
"5 of 18 here" on the Instance list, "6 of 31" on the Audit Log.

The owner chose, on 2026-09-26, from drawn previews:

- **Each list keeps its own filters.** Filtering the Instance list leaves the Audit Log as it was,
  so neither looks empty for a reason set on the other.
- **Filters last until the moderator changes instance.** They are the drive loop's state, cleared
  in `OverlayDriver.EnteredInstance` with the scroll position and the open person. Nothing is saved
  to `settings.json`.
- **Name search in the headset and on the desktop window** (§3).
- **"already here"** for people who were there before the moderator, the Audit Log's own words.

## 2. Join times

The server's roster (`/companion/context`) carries no join time, and asking a server for one would
be a new read. The companion already has it: `InstanceSessionTracker` sees every join in VRChat's
log. It now keeps `ArrivedAt`: VRChat's timestamp for anybody who joined while the moderator was
there (the moderator included), and null for everybody in the arrival burst, who were already
standing there. That is the same split the reports make (people already in an instance design), so
the panel and the server never disagree about who arrived.

It is state, not a report, so replayed history builds it too: a companion started in the middle of
a session still knows when the people who came in after the moderator arrived. It goes when the
instance goes, and with `ForgetSession`.

The drive loop is handed it each tick (`OverlayDriver.ArrivedAt`), turns VRChat's zone-less
timestamps into instants with `LogTimestampConverter`, and puts only the roster's people on the
screen. A person on the server's roster the log never mentioned gets no time, and no time window
claims them. Nothing about arrivals is sent anywhere.

The row says "<1 min", "12 min", "1 h 15 min" or "already here". The screen carries the drive
loop's clock (`OverlayScreen.Now`), and `LooksTheSameAs` compares only its minute, and only on the
two list screens, so the panel is drawn again at most once a minute for the clock.

## 3. Typing a name

Typing in VR is hostile, which is why every other filter is taps. A name is the one thing that
cannot be a list of choices.

- **Desktop overlay window.** While the Name filter is open, typed letters go into it (j and k
  included, which otherwise scroll), Backspace removes one, Enter closes it. The window keeps what
  is being typed itself, because the loop draws four times a second and two keys between draws
  would otherwise each start from the same old text.
- **SteamVR.** Tapping Name puts SteamVR's own keyboard up (`ShowKeyboardForOverlay`, slot 75 of
  `IVROverlay_028`, modal, buffered, 32 characters) with the current name in it; tapping the name
  box does it again. `Done` arrives as `VREvent_KeyboardDone` (1202) on the overlay's own event
  queue, and the text is read with `GetKeyboardText` (slot 76). Slots were read from Valve's
  `openvr_capi.h` at v2.15.6, the same header the existing slots came from.
- **OpenXR (WiVRn, Monado).** There is no keyboard an overlay can ask for, so the panel leaves Name
  off — unless a name was typed on the desktop window, because then it is hiding people on the
  headset too and has to say so.

A name matches in plain letters as well as as written (`NameNormalizer.Searchable`), so `alex`
finds 𝕬𝖑𝖊𝖝.

## 4. Left out

- **Legend and VRChat Team** are not offered as ranks: both are all but gone, and nine choices wrap
  the strip onto a second line. A person holding one shows while no rank is picked.
- **The Audit Log still draws its newest ten** that pass the filters; it has no scrolling.
- **The wrist card** is unchanged: it shows the head count, never a list.
- **The main window's Audit Log page** keeps its own chip filters (`EventFilters`); unifying the two
  was not asked for.
