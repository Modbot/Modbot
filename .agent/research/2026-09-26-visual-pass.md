# Visual pass over the moderator web app

- **Date:** 2026-09-26
- **Covers:** a real, running demo copy of `src/Modbot.Web` on `staging`, built fresh in a separate
  Docker Compose project (`modbot-demo`, port 8081), with the uncommitted changes to `Members.tsx`
  and `Requests.tsx` included.
- **Method:** headless Chrome, driven over the DevTools protocol from a Node script (no browser
  extension, no computer-use tool). 110 real screenshots at desktop (1440×900), phone (390×844,
  2×), and VR density, in both themes. Every image was opened and read, not just captured.
- **Read first:** `CLAUDE.md`, and the earlier code-only review at
  `.agent/research/2026-09-25-ux-review.md` — that review had no working screenshots and said so.
  This pass had real ones, and mostly confirms it rather than replacing it.

Screenshots are at
`C:\Users\Zuelatak\AppData\Local\Temp\claude\G--Modbot\3460c108-a68b-4d30-9883-e697dc456870\scratchpad\visual-pass\shots\`
(left there, per instructions). File names below are relative to that folder.

---

## Verdict

The Console look, the renamed labels, the case files and the charts all look as good live as they
read in the code — this is a well-made app. But the demo build, which is the version an owner or a
new moderator actually opens first, is currently broken in two ways that a screenshot makes
impossible to miss and the code alone did not show:

1. **Every page carries a permanent, wrong, top-of-screen alarm** saying evidence uploads are
   refused because "the evidence store is not there" — a condition that is actually only true for
   the first 30 seconds after the container starts.
2. **None of the three actions a demo exists to let someone try — approve a join request, kick, and
   ban — can be completed.** All three fail with the same raw, technical error, not a clear refusal.

Both would read to an owner exactly the way `CLAUDE.md` warns about: "anything that looks
unfinished or inconsistent." Both have a specific, findable cause in the code, given below.

---

## Findings, ranked by how much they'd hurt

### 1. A permanent false alarm sits on top of every single page

**What a moderator sees:** the moment the demo opens, and on every page after, a full-width red
strip reads:

> Modbot: the evidence store is not there
> 'the Modbot database' holds no store marker. Expected store: 01a0dd66-7175-7abd-af8d-4c8387aea5df
> Found: no store marker. Uploads are refused until this is sorted out. Open Settings, Evidence.

**Screenshots:** it is visible in essentially every capture — see `desktop-dark-members.png` for a
plain example. On a phone it is far worse: in `phone-dark-members.png` and `phone-dark-bans.png`
the banner's five wrapped lines fill roughly the top third of the screen, pushing the page title,
the filters and the first row of real content below the fold before a moderator sees anything else.

**Why it happens, and why it never goes away.** The demo seeder is supposed to prevent exactly this
(`src/Modbot.Demo/DemoEvidence.cs`, comment above `WriteAsync`): it writes a "store marker" so the
evidence store never reads as freshly lost. But that write happens during the ~30-second background
history seed, not the instant startup seed. The container's own log shows the gap directly:

```
11:07:56 WRN A critical notification (modbot.evidence.store-missing) could not be sent on any
             channel; it is waiting for 2 account(s) to sign in
11:07:56 INF Evidence store: 'the Modbot database' holds no store marker.
...
11:08:26 INF Demo history written in 30s.
```

The marker gets written 30 seconds after the alarm fires. In a real deployment that would be fine —
the health check re-probes and the alarm clears. But the banner shown to a moderator
(`src/Modbot.Web/src/components/WaitingAlertsBanner.tsx`) is not that live health check. It is a
different thing: "a critical alert nobody could receive... the one place it is certain to be seen
is the moment they sign in" (the component's own comment). A demo has no sign-in — everyone is
already an administrator — so the one moment this banner is built to appear at never naturally
happens, and the one-time alarm from the 30-second startup gap simply never gets delivered-and-
cleared. It was still showing, with the exact same wording, in screenshots taken more than 13
minutes after the container started.

This will happen on **every fresh demo and every scheduled demo reset**, not just this one.

**Impact:** high. It is the first thing anyone sees, it says the worst possible thing for a
moderation product ("your evidence is not safe"), and it is simply not true by the time a real
person is looking at it.

### 2. None of the demo's three headline actions can be completed

**Join requests page ("Requests" in the sidebar) fails outright.** Opening it shows nothing but:

> ⬛ The server answered 503.

See `desktop-dark-requests.png` (and it happens identically on every visit — the same error is in
the container log every single time the page loads: `GET /api/requests responded 503`). There is no
retry button, and no explanation of why — exactly the gap the earlier code review flagged in general
(finding 14) and it happens here on a page a brand-new moderator is likely to click first.

**Ban fails the same way, but only after the dialog has led someone on.** The Ban dialog opens
normally, reason chips and a note field work fine (`desktop-dark-ban-dialog.png`), and the note
field even fills in correctly (`desktop-dark-ban-dialog-filled.png`, taken while chasing this down).
Pressing the dialog's own **Ban** button, though, always ends here:

> ⬛ The server answered 503.

— see `desktop-dark-ban-result.png`. This happened both times a ban was completed during this pass,
on two different made-up people.

**Root cause (same one, in two places).** Both `POST /api/requests` (well, `GET`) and
`POST /api/moderation/ban` have a guard for "this deployment was never wired up to act in VRChat" —
a real and useful check for an unfinished self-hosted install — that renders its refusal with
`Results.Problem(...)`:

```csharp
// src/Modbot.Api/Features/Requests/RequestEndpoints.cs
return Results.Problem(
    "This deployment is not set up to read join requests.",
    statusCode: StatusCodes.Status503ServiceUnavailable);
```

```csharp
// src/Modbot.Api/Features/Moderation/ModerationEndpoints.cs
return Results.Problem(
    "This deployment is not set up to act in VRChat.",
    statusCode: StatusCodes.Status503ServiceUnavailable);
```

`Results.Problem` writes ASP.NET's standard shape (`type`, `title`, `status`, `detail`). But the
web app's error reader only recognises one shape:

```ts
// src/Modbot.Web/src/lib/api.ts
const message =
  typeof body === 'object' && body !== null && 'error' in body
    ? String((body as { error: unknown }).error)
    : `The server answered ${response.status}.`
```

Neither "This deployment is not set up to read join requests." nor "...to act in VRChat." ever
reaches the screen. A moderator sees a bare status code instead of a plain sentence. (The
*other* refusal path in the same ban endpoint, `catch (ModerationRefused refused)`, does use
`Results.Json(new { error = ... })` and would have shown correctly — this is specifically the
"nothing is wired up" branch that gets it wrong.)

**Kick was not completed** in this pass (its dialog was only opened and cancelled,
`desktop-dark-kick-dialog.png`), but it is mapped through the exact same endpoint helper as Ban, so
it almost certainly fails the identical way. Unban, on the Bans page, likely does too, for the same
reason.

**Impact:** high. This is not a cosmetic gap — it means a demo cannot actually demonstrate the
product doing the thing it's for. An owner deciding whether to trust Modbot, per the earlier
review's own framing, cannot even complete a practice ban.

### 3. The person popup still doesn't answer "have we dealt with them before" — now confirmed on screen

The earlier code review's finding 1 said the ban/repeat/notes/flags picture is scattered across the
popup with nothing above the fold. The real screenshots bear this out exactly:

- `desktop-dark-person-overview.png` and `phone-dark-person-overview.png`: the only sign of standing
  anywhere is a plain "Member" role chip; ban status is not shown at all for a member in good
  standing (correctly, since this one has none — but there is no positive "no history" signal either,
  it's just absent).
- `desktop-dark-ban-dialog.png`: banning is a full dialog reachable only from the bottom of the left
  column, exactly as the code review described.
- Flags genuinely cannot be checked from the popup, and could not be checked in this pass either —
  see finding 5.

This is a real, confirmed problem, not a new one — screenshots simply prove what the code review
inferred.

### 4. Flags could not be exercised in the demo — a real limit, not a bug

`desktop-dark-flags-list.png` shows "No flags" under every tab. The demo seeds no AI key, and
flags come only from AutoMod, which needs one (`docs/content/docs/self-hosting/demo-mode.mdx`: "With
no key set, AI is simply off"). This is a known, correctly-refused limit, not something that looks
broken, so per the brief it is not counted as a bug. It does mean the flag **Dismiss** confirmation
dialog could not be captured at all — there is nothing to dismiss.

### 5. The things that are fine, confirmed

Worth saying plainly, since a review like this can read as all-complaints:

- **Today's renames all read correctly in the running app.** Settings tabs are plain words —
  "Server", "People and roles", "Modbot's VRChat login", "VRChat proxy" — see
  `desktop-dark-settings-data.png` through `desktop-dark-settings-purge.png`. The person popup shows
  "Activity" and "Profile changes" (`desktop-dark-person-activity.png`,
  `desktop-dark-person-profile-changes.png`), the sidebar shows "Modbot's log"
  (`phone-dark-nav-menu.png`), and the page once called "Sync health" is now plainly "Health"
  (`desktop-dark-health.png`, `desktop-dark-health-full.png`) with a sentence-case verdict at the
  top, not "gate state: Working".
- **The note Take-back and Discord Unlink confirmations both work and read well** —
  `desktop-dark-note-takeback-dialog.png`, `desktop-dark-discord-unlink-dialog.png`. Both name the
  person and the exact thing being undone.
- **The world and instance popups are clean and functional**, with a working "Back to the instance"
  link — `desktop-dark-instance-popup.png`, `desktop-dark-world-popup.png`.
- **Light theme holds up.** `desktop-light-members.png`, `desktop-light-bans.png` and the rest look
  like the same app, not a half-finished second theme, and the evidence-store banner itself is
  perfectly readable in light mode too.
- **VR density does not truncate the sidebar** at 1440px, contrary to what the code review
  predicted from reading the CSS alone — see `vr-dark-members.png`. (It may still happen at a
  narrower VR-mode width than this pass tested; this is a partial disproof, not a full one.)
- **The Actions sheet on a phone is still just a keyboard cheat-sheet**, confirming code review
  finding 7 on screen: opened from the Members page, `phone-dark-actions-sheet.png` offers "Next
  row", "Previous row", "Search", "Add a filter" — nothing about the page's actual moderation
  actions.
- **Analytics pages are genuinely good.** The Discord, Worlds and Team analytics pages
  (`desktop-dark-analytics-server.png`, `-worlds.png`, `-team.png`) are clean, readable dashboards
  with real charts — easily the best-looking pages in the app, and a fair thing for the README to
  lead with.

### 6. Minor / matter of taste

- Tables on a phone (Members, Bans) keep the name column pinned and let the rest scroll sideways
  (`phone-dark-members.png`, `phone-dark-bans.png`). The earlier code review explicitly praised this
  pattern ("one app, nothing dropped on a phone") — it's mentioned here only because it's visible in
  almost every phone screenshot, not as a new complaint.
- The person popup's JSON tab (`desktop-dark-person-json.png`) prints picture fields as long,
  URL-encoded SVG data straight in the record dump. That is a correct, honest rendering of the raw
  data — nothing to fix — but it is not a pleasant thing to land on if a moderator taps JSON by
  accident.
- Whether the sidebar's Flags/Reviews counts and the "(N)" browser-tab title work could not be
  checked by screenshot at all — a tab title isn't part of a page capture, and Flags had nothing to
  count in this demo. Worth a quick manual look rather than relying on this pass.

---

## Screenshots

110 PNGs in the folder above, covering: every sidebar page (desktop dark/light, phone dark, and VR
dark for Members/Live/Flags/Bans/Audit log); all 14 Settings tabs; a case file page, full-length;
the Health page, full-length; nine of the ten person-popup tabs at desktop dark (Messages needed a
Discord-linked person with a stored message, Cases needed one with a case file — the ones opened
had neither, so those two tabs show correctly-empty states rather than populated ones); the person
popup on phone and in VR; the command palette; the phone nav menu and Actions sheet; the world and
instance popups; and the Kick, Ban, Discord-unlink and note Take-back confirmation dialogs. The
Flags Dismiss dialog is the one capture on the list that could not be taken, for the reason in
finding 4.

## README and docs screenshots

Replaced in place, all re-shot from this same demo at the original 1440×900, dark theme, matching
each image's caption:

| File | Shows | Source capture |
|---|---|---|
| `assets/live.png` | The Live page | `clean-live.png` |
| `assets/person-popup.png` | A person popup over the page | `clean-person-popup-2.png` |
| `assets/audit-log.png` | The audit log | `clean-audit.png` |
| `assets/analytics.png` | Discord analytics (member count, joins/leaves, messages/day) | `clean-analytics-server.png` |
| `assets/discord-members.png` | Discord members | `clean-discord-members.png` |
| `docs/public/screenshots/worlds.png` | Worlds analytics | `clean-analytics-worlds.png` |
| `docs/public/screenshots/team.png` | Team analytics, 30 days | `clean-analytics-team.png` |

One deliberate difference from the rest of this pass: these seven were taken **after** dismissing
the evidence-store banner (clicking "Seen"), and the person-popup shot uses a person nobody had
touched during testing. Publishing marketing screenshots of a real, temporary boot-time bug (finding
1) would be worse than the stale screenshots they replace; the underlying app was not changed to get
these, only a notification already meant to be dismissible was dismissed. All seven were still
double-checked pixel-for-pixel (1440×900) against the originals they replace.

Nothing else in `README.md` or `docs/` needed reshooting — no other embedded image shows the running
app.

## What didn't work

- The flag Dismiss confirmation dialog: not capturable, no open flags exist without an AI key
  configured (finding 4) — a real limit, not a tooling failure.
- The note Take-back dialog needed a note to exist first; none of the seeded demo people had one, so
  one was added and immediately taken back to capture the dialog (`desktop-dark-note-takeback-dialog.png`).
  That test note is still attached to one demo person (Marblelynxish, `usr_000030de-0000-0000-7a01-000000000000`)
  in the now-deleted demo database — harmless, and gone with the demo stack.
- The first attempt at completing a ban did not select a reason chip, so it silently did nothing;
  the corrected capture (finding 2) is the one on the file list above.
- Everything else on the requested capture list was taken successfully; nothing else timed out or
  came back blank.

## Clean-up

- `docker compose -p modbot-demo down -v` was run; that project's containers, network and volumes
  are gone.
- The Node capture scripts and every headless Chrome instance they started were confirmed stopped
  (checked by process id; none left running).
- `docker compose -p modbot ps` (the user's own stack, port 8080) was checked afterward and is
  untouched: `modbot-modbot-1`, `modbot-postgres-1` and `modbot-seq-1` all still `Up`.
- Screenshots are left in place in the scratch folder named at the top of this file, as instructed.
