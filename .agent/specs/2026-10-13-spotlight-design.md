# Modbot — Member spotlight

- **Date:** 2026-10-13
- **Status:** Built with this document (backend, Settings → Spotlight, the chooser on the Marketing page,
  the Spotlight button on `/profile`).
- **Covers:** a weekly post that features one member who opted in, with their profile card and a short
  note staff write; who is picked, what the card shows, where it goes
- **Depends on:** member profiles design (the fields, the three levels of who sees an answer, the
  `/profile` card), the posts design (drafts, the senders, Pause all posting), the Twitch design (the
  shared "Where it goes" choices), the foundation (§4.4 the clock, §5.5 a purge erases a person)

## 1. Decisions (owner, 2026-10-12)

| # | Decision |
|---|---|
| 1 | **Who is picked:** an admin setting. Either **Modbot picks** (random) or **Staff pick**, from the members who opted in. |
| 2 | **What is shown:** the member's profile card plus a short note staff write. |
| 3 | **Where:** Discord, the VRChat group and any other connected place (Bluesky today), each optional. |
| 4 | **Nothing goes out by itself.** Modbot makes a Marketing draft; staff write the note and send it as for any post. |

## 2. Opting in

A member opts in with the **Spotlight: On / Off** button on their own `/profile` card, beside
Findable. It is **off** for everyone to start, and only the member can press it. It is one column on
the member's profile row, `member_profile.spotlight`; a member who never touched the card has no row
and is not opted in.

A member can be featured only while they are opted in, still in the server (`left_at` empty) and not
a bot.

## 3. Settings → Spotlight

Needs Manage settings, like Posts and Twitch. Every change is a settings change in the audit log
(`setting = spotlight`).

| Setting | Notes |
|---|---|
| On | Off to start. While off, a pass reads the settings row and does nothing else. |
| Day, hour, time zone | Day 0 (Sunday) to 6, hour 0 to 23, an IANA zone (empty is UTC). Monday 09:00 UTC to start. |
| Who picks | `random` (Modbot picks, to start) or `staff`. |
| Where it goes | The sites ticked, with their own choices: the same `TwitchPostPlaces` the Twitch live post uses and the same "Where it goes" picker. Nothing ticked to start. Each site ticked is checked the way the Marketing tab checks a post (the channel is in the server, the role may be mentioned, the VRChat group and Bluesky are set up); all problems are returned at once. |

**Turning it on, or moving the time, never makes a draft straight away.** Saving marks the latest
moment the settings name (at or before now) as already dealt with, so the first draft is the next
one.

## 4. The weekly pass

`SpotlightPass` runs once a minute (`SpotlightService`, its own loop). When the latest moment the
settings name is newer than `settings.spotlight_handled_through`:

1. **Claim.** One update on the settings row sets `spotlight_handled_through` to the moment, in the
   same transaction as the draft. A second pass, or a second copy of Modbot, finds it taken and does
   nothing; a pass that fails gives the claim back with everything else.
2. **One draft a week.** The week is the Monday of the moment in the settings' zone. `spotlight_week`
   has a unique index on it, so a week that already has a row (the day was moved within the week)
   gets no second draft.
3. **Random mode.** Picks from the members who opted in (§2) with `MemberSpotlight.Pick`:
   - A member featured in the last **N = 8** weeks is skipped. A week with no member does not count.
   - **A small pool shrinks N**: N is `pool size - 1`, at most 8, so someone is always left. With one
     member N is 0 and they are featured every week.
   - **If nobody opted in, the week is skipped**: no row, no draft, one line in the log. The moment is
     still claimed, so the next minute does not look again.
4. **Staff mode.** The draft is made with the title "Member spotlight", no text and no member; the
   week row has no member yet (§5).
5. **Audit.** The draft is recorded as `PostCreated`, with no person as the actor, the same fact the
   Marketing page writes. Picking a member later writes `PostChanged`.

The draft is a normal Marketing post (kind `spotlight`, status draft) with the sites ticked in
Settings. Problems the Marketing tab would refuse on are not refusals here (a Bluesky text too long,
a site that is off); the composer shows staff what to fix when they open it.

## 5. Staff mode: picking the member

When a spotlight draft has no member, the Marketing page shows a small card above its tabs, for
people who can manage posts: a list of the members who opted in (by name) and a **Choose** button.
Choosing fills the draft with that member's card and avatar and records the week's member; then the
note is written and the post sent from the list below as for any post. Anything staff typed in the
draft first is kept: the card goes above their text (cut to fit, never their words), their own title
stays if they changed it, and their own picture stays if they set one.

The note is typed into the draft's text like any post, by hand. Modbot does not write it.

## 6. What the card shows

The title is `Member spotlight: <name>` (the name the server shows, cut to a post's title length). The
text is one line per answered field, `Field: answer`, in the field order. The picture is the member's
Discord avatar, fetched and kept like any post picture; a member with no avatar, or one that cannot
be fetched, is featured without a picture.

**Only the answers the member opened to Members are in it.** The card is read as another member would
read the profile (`ProfileViewer.Member`), so nothing at Staff or Only me level is ever in the draft,
whoever made it. Staff add their note by editing the text.

## 7. Tables

### 7.1 `member_profile` (new column)

| Column | Notes |
|---|---|
| `spotlight` | Boolean, false. The member's opt-in (§2). |

### 7.2 `spotlight_week`

One row per week that has a spotlight. Also the history that random mode reads to skip members
featured lately.

| Column | Notes |
|---|---|
| `id` | Guid. |
| `week` | The Monday, a date. Unique (`ux_spotlight_week_week`). |
| `discord_user_id` | The member featured; text, never parsed. Null until staff pick one, and again after a purge. |
| `post_id` | The Marketing draft. Set null if the draft is deleted; the week and the member stay in the history. |
| `created_at`, `picked_at` | `picked_at` is `created_at` in random mode and the time of the pick in staff mode. |

### 7.3 `settings` (new columns)

`spotlight_on` (false), `spotlight_day` (1), `spotlight_hour` (9), `spotlight_time_zone` (null),
`spotlight_mode` (`random`), `spotlight_places` jsonb (`{}`), `spotlight_handled_through` (null).

## 8. API

| Method and path | Needs | What it does |
|---|---|---|
| `GET /api/settings/spotlight` | Manage settings | The settings (§3) and the VRChat group's roles for the picker. |
| `PUT /api/settings/spotlight` | Manage settings | Changes any of on, day, hour, timeZone, mode, places; a part left out keeps what is stored. A bad value is a 400 listing every problem. |
| `GET /api/spotlight` | View posts | The newest spotlight draft with no member (`pending`: id, week, postId), with the members who opted in (id, name). `pending` is null when none waits. |
| `POST /api/spotlight/{id}/member` | Manage posts | Picks the member (`discordUserId`) and fills the draft. 400 if that member has not opted in or has left; 404 for an unknown spotlight; 409 once a member is picked, and when the draft cannot be edited (cancelled, being sent, gone out). |

## 9. Purge

A purge of a person (foundation §5.5) removes the opt-in with the rest of their profile row, and sets
`spotlight_week.discord_user_id` to null for the weeks they were featured, whether the purge is by
Discord id or by a linked VRChat account.

**Drafts that have not gone out are deleted whole.** Such a draft holds the member's name, their
answers and a copy of their avatar. The card and the note staff typed share one text and cannot be
told apart, so the draft is deleted, and its avatar copy with it unless another post or an event
uses the same picture. "Not gone out" means no site of the post is sending, being checked, posted,
removed or failed. A draft that went out, or may have, is a post on Discord, VRChat or Bluesky and is
left as it is. The week row stays either way, so no second draft is made for that week.

## 10. Not built

- A note typed in Settings or generated by AI: the note is written in the draft.
- Choosing a member who has not opted in. Staff cannot, by design.
- A different count than 8 weeks for the repeat rule. It is a constant.
