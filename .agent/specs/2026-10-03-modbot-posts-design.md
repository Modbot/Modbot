# Modbot — Posts and the Marketing tab

- **Date:** 2026-10-03
- **Status:** Step 1 (posts, the Marketing tab, Discord) built with this document. Steps 2 to 4
  (VRChat group posts, Bluesky, event posts on the shared model) are designed here and not built.
  The owner chose every recommended answer in §10 on 2026-10-03, and added one rule: no automatic
  members-only rule; every destination is a per-post tick, unticked when the composer opens, and
  the preview is the gate.
- **Covers:** one posting system for hand-written posts and, later, the calendar's event posts:
  the tables, the shared sending rules, at most once on every site, the Discord sender, the
  Marketing tab, its composer and preview, permissions, the Settings topic, Health, facts, what
  leaves the server
- **Depends on:** the calendar (2026-09-15, pictures §15, preview §14.2, places §3), the foundation
  (§4.3 rate limits, §4.4 the clock), the Discord embeds and gateway, the VRChat page's Posts tab
- **Replaces:** the Bluesky design's `calendar_social_post` table (§6)
- **Source:** the read-only exploration report `2026-10-03-marketing-tab-design.md` (the owner's
  reports folder), which carries the facts table and sources this spec relies on

---

## 1. Why one system

A post written on the Marketing tab and a post the calendar makes for an event have the same parts:
words, maybe a picture, the sites it goes to, an id and a link on each site, at most once, Try again,
Delete. Three loops already post one-off messages to Discord and VRChat, each with its own rules for
once, refusals and Failed (`CalendarFirstJoinPost`, `CalendarFirstJoinVRChatPost`, the cancel line in
`CalendarDiscordPublisher`). One sender per site, and one list of everything that went out publicly
from Modbot, with one Health card and one pause switch, removes the copies (decision 1).

The calendar's **live copies** stay as they are: its VRChat calendar entry, its Discord event and the
channel card edited in place are not posts, because they are rewritten whenever the event changes.

## 2. Tables

### 2.1 `post`

| Column | Notes |
|---|---|
| `id` uuid | |
| `title` text null | Bold on Discord's first line; VRChat needs one (step 2); event posts fill it from the event. |
| `text` text | The words, before any destination's own text. Up to 8000 characters. |
| `picture_id` uuid null | A row of `calendar_cover_picture`, which holds every picture Modbot keeps for posting. The hourly sweep counts it as a use; a picture deleted under a post leaves it with none. |
| `status` | `draft`, `scheduled`, `cancelled`. Sent, Failed and Sending come from the destinations (§3). |
| `send_at` timestamptz null | When it goes. "Now" is saved as the time of saving. A draft may keep a planned time. |
| `time_zone` text | The zone the time was picked in: the browser's, or the event's for event posts (decision 13). |
| `event_id` uuid null | Set for event posts, and for a post linked to an event. |
| `kind` text null | `announced`, `reminder`, `live`, `cancelled` for posts the calendar makes (step 4); null for hand-written posts. |
| `date_starts_at` timestamptz null | The date a reminder, live or one-date cancel post is about (step 4). |
| `version` int | Raised by every write a person makes, and by the sender's claim. The EF concurrency token (§4.3). |
| `created_by_user_id`, `created_at`, `updated_at`, `cancelled_at` | |

Two partial unique indexes keep the calendar from making a second post of one kind, without
`NULLS NOT DISTINCT`, which older PostgreSQL lacks: `(event_id, kind) WHERE kind IS NOT NULL AND
date_starts_at IS NULL`, and `(event_id, kind, date_starts_at) WHERE kind IS NOT NULL`.

### 2.2 `post_destination`

| Column | Notes |
|---|---|
| `id`, `post_id` | Deleted with its post. |
| `network` | `discord`; later `vrchat`, `bluesky`, and others (`mastodon`, a web address). |
| `target` | Discord: the channel id. VRChat: the group id. Bluesky: the account's DID. Kept so an edit or delete goes to the same place after settings change. Opaque. |
| `options` jsonb | Discord: `roleId`, `publish`. VRChat: `visibility`, `roleIds`, `notify`, `imageId`. Bluesky: `langs`, `cid`. |
| `title_override`, `text_override` | Null means the post's own. Discord's "Own text". |
| `state` | `waiting`, `sending`, `checking`, `posted`, `failed`, `removed`, `skipped`. |
| `client_key` | Bluesky's record key, made once (step 3); later Discord's nonce. |
| `external_id`, `link` | The id on the site (opaque) and the post's https address. |
| `sent_title`, `sent_text` | Exactly what went out, for the look and the audit log. |
| `sent_at` | The last attempt, written before sending. |
| `posted_at`, `published_at` | `published_at`: when a Discord post was published to followers. |
| `error`, `error_at`, `missing_permission` | The site's own words. |
| `reply_to_id`, `remove_at` | Bluesky's reply under an earlier post; the calendar's cancel line's own removal (step 4). |
| `check_at` | When Modbot next looks on the site for a post that got no clear answer. |
| `may_be_sent` | The last attempt got no clear answer and no look has settled it: the post may be on the site. |
| `send_if_missing` | A person pressed Try again on such a post: when the look finds nothing, send. |
| `updated_at` | For a waiting row, when it started waiting (§4.1). |

Unique `(post_id, network)`: one Discord channel per post (decision 16).

`check_at`, `may_be_sent` and `send_if_missing` are not in the report's table; they are what the
report's "look a minute later", "Try again looks first" and "never resent on its own" need written
down, so a restart loses none of it.

## 3. States and the lists

| List | Shows |
|---|---|
| **Scheduled** | `scheduled`, a destination `waiting`, `sending` or `checking`. Soonest first. |
| **Sent** | `scheduled`, every destination `posted`, `removed` or `skipped`, at least one posted or removed. Newest first. |
| **Drafts** | `draft`. Newest change first. |
| **Failed** | `scheduled`, any destination `failed`. Also in its other list. |

Cancelled posts are left out, and shown with the **Cancelled** chip. The rule is written once in
Core (`PostRules.ListsOf`) and the list query is the same rule in SQL; the web app has the same rule
for where a saved post goes.

What a destination shows: its state, `sending` for `checking` too, or for a waiting one whose site
sends nothing now, `paused`, `off` or `notSetUp`.

## 4. Sending

### 4.1 Shared rules (`Modbot.Core/Posts`)

- **`PostRules`**: due is `scheduled`, `send_at ≤ now` (`IModbotClock`), destination `waiting`.
  Nothing is sent while Pause all posting is on, the site's switch is off, or the site is not set up
  (Discord: a server id and a connected bot); the row waits and shows why. One send per site per
  pass, at most 10 an hour per site. **Late** (decision 5): due more than an hour ago means not
  sent, Failed with "Not sent on time." and Post now. Due is the later of `send_at` and the moment
  the destination started waiting, so Try again and Post now start the hour again.
- **`PostTexts`**: the text each site gets, the counters and Discord's mention line and bold title.
  Tidied the way Discord keeps it (`\n` line ends, trimmed), so the look compares like with like.
  The senders, the preview and the counters all use it, so the preview is what goes out.
- **`PostClaim`**: writes `sending`, `sent_at` and exactly what is about to go out, raises
  `post.version`, saves; only then may the caller send. A post written since it was read fails the
  claim, and nothing is sent until it is read again.
- **`PostChanges`**: what Try again, Post now and Cancel post do to the destinations.

### 4.2 Discord (`Modbot.Discord/Posts`)

- **The message**: `<@&role>` on its own line when a role is picked, then `**Title**`, then the text.
  The picture goes as a file. One role, never @everyone; pinged on the first send, never on an edit.
- **Publish to followers**: Announcement channels only; `CrosspostAsync` once the post is in. A
  refusal leaves it Posted and says Not published; Try again does that step alone.
- **Gateway**: `SendPostAsync` (sends with `RetryMode.AlwaysFail`, says when the answer is not clear),
  `EditPostAsync` (text only, keeps files, pings nobody), `PublishAsync`, `ReadRecentAsync`.
- **At most once** (decision 3, A):
  1. Claim, then send.
  2. An id: `posted`.
  3. A clear refusal (4xx other than 429): `failed` with Discord's words.
  4. A rate limit, or the channel could not be looked up: nothing was made; back to `waiting`, its
     waiting time unchanged, and not tried again for two minutes (`check_at`), so the posts due
     after it are not held up.
  5. No answer, a timeout, a 5xx: `checking`, `may_be_sent`. A minute later, read pages of 50
     messages after a Discord id made from `sent_at` minus a minute, up to four, until a message
     later than `sent_at` plus two minutes or the channel's newest has been seen (the whole window).
     A message by the bot whose text equals `sent_text`, with the same number of files, held by no
     other destination, is adopted as `posted`.
  6. Nothing found in the whole window: `failed`, "Discord did not take the post.".
  7. The look cannot be made, or the pages ran out before the window did: stays `checking`, looked
     for again in 15 minutes; an hour after the looking began (the unclear answer, or Try again),
     `failed`, "Could not check the channel.". Not found is never an answer until the whole window
     was read.
  8. A row left `sending` for two minutes was cut off by a restart: it is looked for, never resent.
  9. **Never resent by itself.** Try again on a post that may be on Discord looks first, and sends
     only when it is not there.
- **The loop**: `PostDiscordService`, every 20 seconds while the bot has a ready session; a sibling
  of the calendar's Discord loop with the same shape, so a calendar pass that throws does not hold
  posts up.
- **Later hardening**: a direct REST create with `nonce = client_key` and `enforce_nonce = true`.

### 4.3 Edits and the version

Every change a person saves says which `version` it read. A change while a destination is `sending`
or `checking` is refused with "This post is being sent."; one saved after the claim fails its own
concurrency check the same way. A post that went out is edited on each site; one a site may already
have is settled by Try again first. Changing the whole post puts its failed (not maybe-sent)
destinations back to waiting.

### 4.4 After it went out

Edit and Delete on a site are one call each through `IDiscordPostActions`, made at once so the person
sees whether it worked, the bot's live session behind it. A message already gone counts as deleted.
What the site did is then written to the row by reading the post again (up to three times on a
version conflict) while the destination is still that message, and its fact is written whatever
happens; a row that became something else meanwhile answers 409 "This post changed. Try again.".

A channel must be one of the server in settings as the bot last listed it: "That channel is not in
the Discord server." otherwise.

## 5. The Marketing tab

### 5.1 Navigation and permissions

- Sidebar: **Marketing**, Community, after World lists, before Giveaways. `/marketing/scheduled`,
  `/marketing/sent`, `/marketing/drafts`, `/marketing/failed`; `/marketing` opens the first. `g q`.
  Palette words: posts, announcements, bluesky, social, schedule post.
- **See posts** (`ViewPosts`, bit 52): the page and every post. **Manage posts** (`ManagePosts`,
  bit 53): write, schedule, change, cancel, Post now, Try again, edit and delete on a site (decision
  2). Neither is in the built-in roles. **Manage group posts** stays the VRChat page's Posts tab
  (decision 17). Ticking a post on an event will need Manage calendar and Manage posts (step 4).
- Live: a person with See posts is sent `modbot.post.*` facts on the live stream, without the
  operational log they sit in, so the list redraws.

### 5.2 List

Four lists with counts; each row the time in the viewer's zone (the post's zone after it when
another), the title or first line, who wrote it, a badge per destination, the event's name when
linked, and the actions open to the post now. **Cancelled** chip. **New post**.

### 5.3 Composer

A dialog in the event form's shape: Write · Preview, buttons pinned at the foot (Schedule, or Post
now when Now is picked; Save draft; Cancel).

- Title, Text, a counter per ticked site ("Discord 212 / 2000", UTF-16 units, red when over).
- Picture: Choose or a picture link (fetched through Modbot's picture-link guard), the calendar's
  crop box with 16:9 · 1:1 · As it is, uploaded to `POST /api/posts/picture`.
- Where it goes: chips, all off when the composer opens for a new post, every time. A site not set
  up shows Not set up and one switched off shows Off, linking to Settings. Step 1 shows Discord only:
  Channel, Mention role, Publish to followers (Announcement channels only), Own text with Reset.
- When: Now or Later (date, time, time zone).
- No explanatory text; errors are sentences, all of them at once.

### 5.4 Preview

`POST /api/posts/preview` returns what each ticked site would be sent, from `PostTexts`, and what
would stop the post being scheduled. The browser draws Discord's message: the role in its colour,
the bold title, the text with links, the picture (the crop before it is uploaded).

### 5.5 Before and after

Before: Edit (whole post), Post now, Cancel post (confirm; waiting becomes skipped), Delete draft
(confirm), Duplicate (a new draft with every site unticked; decision 6, no repeat). After, per
destination: Open, Edit (title and text, Discord), Delete (confirm "Delete this post on Discord?").
Failed: the words and Try again.

### 5.6 Settings and Health

- Settings, Posts (Connections, Change settings): **Pause all posting** (decision 12: posts only,
  not the calendar's copies) and **Discord posts** (on to start, old installs too). VRChat's switch
  comes with step 2; Bluesky's stays in its own topic and is mirrored here. Audited as settings.
- Health, **Posts** card (See posts): Paused; sites posts wait on that are Off or Not set up;
  failures in the last 7 days; posts looked for longer than 15 minutes.

### 5.7 API

All under `/api/posts`: list, one post, create, change, send-now, cancel, delete a draft, try-again,
edit and delete on a site, preview, picture, picture by id, picture-link, health; and
`/api/settings/posts`. The calendar's event read gains `posts` in step 4.

## 6. Facts

Subject: the post id (Modbot platform). Actor: the person, or none for a send, with `scheduledBy`.
Operational category, moderation retention (kept).

| Fact | Data |
|---|---|
| `modbot.post.create` | the post: title, text, picture, status, `sendAt`, zone, event, destinations and options |
| `modbot.post.change` | title, `changed` (each field's `old` and `new`); `postNow` or `tryAgain` for those |
| `modbot.post.cancel` | title; `deleted` for a draft deleted |
| `modbot.post.send` | network, channel, link, external id, `sentText`, `adopted`, `scheduledBy` |
| `modbot.post.fail` | network, channel, the words |
| `modbot.post.edit` | network, text before and after |
| `modbot.post.remove` | network, by whom, link |

## 7. What leaves the server

| Site | Sent | Seen by |
|---|---|---|
| Discord | title, text, picture file, one role mention | the channel's readers; followers' servers if published |
| VRChat (step 2) | title, text, picture (uploaded to Modbot's VRChat account), roles, visibility, notify | group members, or everyone on vrchat.com |
| Bluesky (step 3) | text, link card, languages; sign-in to the account's server | public, and permanent once copied |

Never sent: member names, user names or ids, counts of people, moderation data. Operator switches:
the per-post ticks (all unticked to start), the per-site switches, Pause all posting, VRChat picture
uploads, removing the Bluesky account.

## 8. Later steps

- **Step 2, VRChat group posts**: `PostVRChatSender` on `groups.posts.write` (Background priority,
  never a 429 resent), written before sending; a 5xx, a 408 or no id is `checking`, and two minutes
  later page 1 of `GetGroupPosts` is read and a post by Modbot's account whose title and text match
  (letters and digits) and is no older than the attempt minus two minutes is adopted; the audit
  log's `group.post.create` adopts too. Composer section (who sees it, roles, notify, own title and
  text), the picture through the existing upload while VRChat picture uploads are on (decision 14),
  edit with the same `imageId` and `sendNotification: false`, delete with 404 as done. The VRChat
  switch.
- **Step 3, Bluesky**: app password first (3a), OAuth for installs with a public address (3b,
  decision 15); a fixed record key with `swapRecord: null`; Cancelled as a reply under Announced
  (decision 7); Check shows whether the account is marked automated (decision 10).
- **Step 4, event posts**: `post_plan` and `post_texts` on the event; `CalendarPosts.Plan` (Announced
  once, Reminder an hour before each date (decision 11) and skipped past the start, We're live on the
  shared first-join check, Cancelled only where Announced posted); a planner step in
  `CalendarService`; the first-join posts and the cancel line move onto posts, the cancel line with
  `remove_at`; Announced texts editable per site (decision 8); the event's picture (decision 9).

## 9. Not checked

- How Discord counts its 2000 characters (UTF-16 here, which never says a message fits when it does
  not); the crosspost limit; whether an edit of a published message reaches followers' copies.
- Discord.Net's own default retry mode in 3.20.1. `AlwaysFail` is set explicitly on post sends and
  publishes, so the answer does not change what Modbot does.
- Everything the report lists as not checked for VRChat and Bluesky.

## 10. Decisions (owner, 2026-10-03; every one the recommended answer)

1. One posting system for events and Marketing.
2. New See posts and Manage posts; an event post tick needs Manage calendar and Manage posts.
3. Discord: written before sending, then look in the channel and adopt; `enforce_nonce` later.
4. VRChat: look two minutes after an unclear answer, adopt a match, else Failed with Try again.
5. Late posts go up to an hour late, then Failed with Post now.
6. No repeat; Duplicate instead.
7. Bluesky: Cancelled as a reply under the announcement.
8. Event post texts: Announced only, per destination.
9. Event post picture: the event's Discord picture, else the world's, else none.
10. Bluesky Check shows whether the account is marked automated.
11. Event reminder: an hour before.
12. Pause all posting stops posts only.
13. The browser's zone with a picker; an event's posts use the event's zone.
14. VRChat post pictures only while VRChat picture uploads are on.
15. Bluesky app password first, OAuth as its own step.
16. One Discord channel per post.
17. The VRChat page's Posts tab stays.

Added by the owner: no automatic members-only rule; every destination is a per-post tick, unticked
when the composer opens, and the preview is the gate.
