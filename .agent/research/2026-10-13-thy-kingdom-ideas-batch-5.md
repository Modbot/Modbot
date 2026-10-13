# Thy Kingdom ideas, batch 5 (2026-10-13): research only

Sent by the Thy Kingdom Driver session on 2026-10-13. The user picked these. Same rules as batches
2-4: check what Modbot already has, research each idea, and build nothing until the user OKs a plan.
Keep each feature generic, with Thy Kingdom as the first user.

## Live review findings (Thy Kingdom's live Modbot, last 30 days; read-only)

Full note: `G:\Thy Kingdom\reports\Modbot live review.md`.

- Existing features that are off or unused: auto-invites off (0 sent) while staff sent 246 invites
  by hand; Discord account linking not set up (0 of 722 linked); join gate off; no welcome steps
  (only 18% of new Discord joiners stay 7 days, 0% active); role/ban sync never run; Marketing and
  Giveaways empty. Spotlight is on (Mondays). AutoMod is on (4 Hub term lists + 3 AI topics), no
  flags yet.
- Discord bot permissions block features: no Manage Roles, Ban Members, Kick Members or View Audit
  Log, and it reads only 34 of 160 channels.
- Relevant to batches 2-4: account-link nudges need linking set up first; smart welcome / welcome
  steps already exist; batch 3 #2 (second-instance alert) may already be covered by the existing
  instance-filling alerts.

## Ideas (all from the stats pages)

1. **No-moderator alert:** ping staff when a group instance runs with no moderator in it. Data: 9 of
   29 instances had no moderator. Pairs with the rota idea (T-10 in the idea inbox).
2. **Repeat-offender view:** before an event, show people acted on more than once recently. Data:
   19% of the 277 people acted on were acted on again within 30 days.
3. **Kick vs warn check:** show whether kicks are door checks (age or verification) or should have
   been warnings first, following the handbook ladder (`G:\Thy Kingdom\handbook-draft.md` §10).
   Data: 242 instance kicks vs 21 warns. The check should tell normal door checks apart.
4. **Join-request wait alert:** ping staff when a join request has waited too long. Data: typical
   wait 6h 1m over 102 decided.
5. **"Interested" vs real turnout per event series:** compare VRChat event "interested" counts with
   in-world people. Data: Watch Party averages 9.1 interested but Popcorn Palace peaked at 11 people
   across 7 instances. Pairs with batch 4 #4 (series health).

Not picked: a door-workload alert (one moderator did ~92% of door/admin actions).

## User's answers
- 1: B + C (2026-10-13): the existing "Instance unwatched" alert gets a head-count setting (instead of a fixed 8) and pings a staff channel the server picks; the alert names who is on shift once the staff rota (idea T-10) exists. Thy Kingdom must switch it on.
- 2: A + B + C (2026-10-13): list of people acted on 2+ times in the last N days (with what happened); a mark on the Live page when one is in an open instance; a staff ping when one joins an instance.
- 3: skip (2026-10-13): no kick vs warn check.
- 4: A (2026-10-13): staff-channel ping when a join request has waited more than N hours (setting).
- 5: covered by batch 4 #4 (A + C: per-series page with Discord interested, VRChat interested and actually came; monthly staff summary; no drop alert).
