# Modbot — World lists

- **Date:** 2026-10-01
- **Status:** Built
- **Covers:** Lists of worlds kept in Modbot, an event whose world is picked from a list, the shuffle
  that picks, and **Next game** during an event
- **Depends on:** calendar design (§2 the event, §4 opening the instance, §8 facts, §13 results),
  foundation §3.1 (no capacity limits), §3.1.1 (ids are opaque), §4.3 (rate limits)

---

## 1. What this is

The maintainer asked for "custom favorite lists for worlds… a game night event that automatically
picks from the list". A **world list** is a name and some worlds, each with the fewest and most
players its game is for (both optional). An event can take its world from a list instead of naming
one, and during the event **Next game** says which world to go to next.

The lists live in Modbot. Nothing is read from or written to VRChat's own favourites.

## 2. The page

**World lists**, in the sidebar under Community, after Calendar.

- Each list shows its worlds (picture, name, players) and which events use it.
- **New list**, **Edit** and **Delete**. A list a draft, scheduled or open event uses cannot be
  deleted; the event has to stop using it first.
- A world is added by searching the worlds Modbot already knows (by name or id), or by pasting a
  world's link or id.

**A pasted world Modbot has never read** is read once with `GetWorld` on `worlds.read`, the class the
world sweep (`WorldSync`) already uses: one a second, measured, resource-scoped to the world. It runs
at interactive priority, because a person is waiting on the form. No new endpoint and no new class:
the read is the sweep's own read, done once, sooner. A 429 cold-stops the bucket and is never retried;
the world is then kept as its id and the sweep names it on its next pass. So is a sign-in wait, no
network, a Cloudflare block, a 401 or a 5xx: Modbot could not ask, so it keeps what was typed. Any
other refusal (400, 403, 404...) is shown in VRChat's words and nothing is stored; text that is not a
link gets "No world by that name or id. Paste its link." instead. A world VRChat refused before (its
row has a refresh error and was never read) is asked about again. The id itself is never checked for
shape: a link is taken apart only to find the id in it (`worldId=` or the part after `/world/`), and
anything else is taken as the id as typed.

**Search VRChat** (2026-10-15). When the known-world search finds nothing for what is typed, a
**Search VRChat** button appears under the box. One press is one `SearchWorlds` (`n=10`, `search=` the
text) on a new class, `worlds.search`: its own lane and bucket, **not measured**, a cautious
placeholder of one search per ten seconds (§4.3.4 of the foundation spec had no number for it), on the
`interactive` backstop. Its own class so a 429 never pauses world naming or head counts
(`worlds.read`). Never retried. A press under ten seconds after the last is answered at once with the
seconds left (`WorldSearchWait`, in memory) and sends nothing; a cold stop, a sign-in wait, no account
or no network answers "VRChat can't be asked right now." Up to ten results show with picture, name and
author; picking one adds it like a known-world pick. Each result is kept as a `VRChatWorld` row (name,
pictures, author), but `LastRefreshedAt` is left alone: a search result is not a full read, and the
world sweep fills in the rest. A world listed publicly also clears an earlier refusal on its row, so
the save check accepts it. VRChat's search finds public worlds only; a private world still needs its
link.
A message under the box (a wait, a refusal) is about the text it was sent for, so it goes away as soon as
the text changes (2026-10-15).

**Saving a list** checks only the worlds it adds (not already on that list): each must have a
`VRChatWorld` row and must not be one VRChat refused (a refresh error and never read). Otherwise the
save is refused and names the world. Worlds already on the list stay, even ones that went private
since. Saving no longer makes placeholder rows for unknown ids.

**Old refresh errors** (2026-10-15). The sweep now writes `RefreshError` only for real VRChat refusals; before
that it wrote one for every failure (no account, network, 5xx), which hid real worlds from Seen and blocked
saving them. The migration `ClearWorldErrorsNeverRead` clears the error on worlds never read
(`LastRefreshedAt` null) so the sweep asks again; worlds that were read keep theirs. It has no undo.

**The event form's list menu** (2026-10-15). A list's name picks the whole list (a shuffle); the arrow beside
it opens its worlds to pick one. Seen has an arrow too (2026-10-15): its worlds are not loaded with the form but when the arrow opens, from `GET /api/world-lists/seen`.

**A list needs at least one world** (2026-10-15). Saving, or editing, a list with none is refused with
"A list needs at least one world." (`WorldListEndpoints.Check`, for both the new and the changed list). A
list that is already empty stays as it is until someone edits it; the scheduler and the pick still treat
it as picking nothing (§5). The migration `RemoveUnusedEmptyWorldLists` (2026-10-15) deletes once the empty
lists that no event (any state), template or shuffle points to; Seen is never touched.

**The Seen list** (2026-10-15). One list made by the migration `SeenWorldList`, id
`WorldList.SeenId`, name **Seen**: every world Modbot has seen, minus the junk. Junk is a world with
`LastRefreshedAt` null and either a refresh error (VRChat refused it) or no name (an id nothing has
confirmed); a world read once stays, whatever the sweep's later errors say. A real row, so events,
templates, shuffles and the delete guard point at it like at any list, but it has **no
`world_list_item` rows**: `WorldListContents.ItemsAsync` works its worlds out each time a pick, a view
or the "has a world" check asks, so there is nothing to edit or go stale. Every world fits any number of
players (no range). `PUT` and `DELETE` on it answer 400 "The Seen list can't be changed."; the 500-world
cap does not apply. The page lists it read-only, after the lists people made; the event form lists it
first under **Pick from a list**. The shuffle over it is the ordinary one (§4): a world seen since joins
the current round, a world that became junk leaves it.

**Players.** Both numbers are optional and at least 1; the most may not be under the fewest. There is
no upper limit: a world's real capacity is not something Modbot knows (foundation §3.1).

## 3. Permissions

| Permission | Allows |
|---|---|
| See calendar | The World lists page, an event's list, the current **Next game** |
| Manage calendar | Making, changing and deleting lists; **Pick again**; **Next game** and **Pick another** |

The calendar's own permissions, because a list exists for the calendar's events. Picking is a write:
it changes what the shuffle has played.

## 4. The shuffle

One shuffle for **each list and each event** (a repeating event is one event), kept in
`world_list_shuffle`: the order this round and which of it has been played. It is in the database, so
a restart picks up where it was.

- **A round** is the whole list in a random order. A pick takes the first world in the order not yet
  played this round, and marks it played. Every world is played once before any world comes round
  again.
- **When every world has been played**, a new round is shuffled. Its first world is never the one just
  played, when the list has two or more.
- **A world added to the list** joins the current round at a random place. **A world taken out** leaves
  the order and is never picked again.
- **Put back.** A pick that is replaced (**Pick again**, **Pick another**) is marked not played again,
  so it is still due this round.

Several picks at once for the same event (the scheduler and a person, or two people) wait on one
advisory lock per event, so two picks never read the same shuffle and play the same world.

## 5. The event's world, picked from a list

The form's **World** gets one more choice, **Pick from a list**, and then a list to pick from.

**When the world is picked: when the date becomes the event's current one** (`OccurrenceStartsAt`):
for a one-off event or a first date, when it is scheduled; for a repeating event, as soon as the date
before it ends. Why then:

- Every place that shows the world shows the current date. The Discord event and the channel post are
  made for each date (calendar §3.2, §3.3), and the instance opens for it (§4). Picked then, the world
  is on all of them from the moment they exist, and never changes under them unless someone says so.
- It gives editors the whole stretch before the date to **Pick again**.
- Later dates are not picked yet, so moving or editing the event never wastes a world.

The pick is written into the event's `WorldId`, so the Discord event, the channel post, the feed, the
instance opening and the results read it exactly as they read a world set by hand. Nothing in those
paths changed. VRChat's calendar has no world field, so VRChat shows nothing different.

**Locked per date** (`world_pick`, one `date` row per event and date, a unique index on the ones not
put back):

- Modbot never changes a date's world by itself.
- An edit that only moves the time moves the pick to the new date, rather than spending another world.
- Changing the event to another list puts the old pick back and picks from the new one.
- **Pick again** (Manage calendar), on the event, while the date is **Scheduled**: puts the world back
  and takes the next one in the order. Once the date is open, its instance may already be open in that
  world, so **Next game** is the control instead.
- An empty list picks nothing; the event has no world until the list has one. Since 2026-10-15 a list
  cannot be saved with no worlds (`Check` refuses it), so this is only a list made before then, or a
  list whose worlds were all deleted another way; it stays as it is until someone edits it.

`CalendarEvent.WorldPickedFor` holds the date the event's world was picked for, so the scheduler's
fifteen-second pass knows which events need a pick without reading another table.

**Results** (calendar §13): a past date of a list event looks for its instance in the world picked for
that date, not the event's current one.

**The feed** has one entry for a repeating event, so its location is the current date's world.

## 6. Next game

On an **open** event that picks from a list, the event popup shows **Next game**; so does the instance
popup when the instance is one of the event's.

**The event's instances** are the open ones among: the instance Modbot opened for the date, and the
managed group's instances in a world picked for the date (its own or a game's) opened from an hour
before the date opened until now. The hour is for a host who opens the first world by hand a little
early. Moving to the next game usually means a new instance, so the newest is where people are.

- **People now** is the head count of the event's instance (`HeadCount`, else `LastUserCount`, what
  the group instance poll already keeps, read the way the instance popup reads it): from the instance
  popup, that instance; from the event, the newest of the event's instances. When there is none, or it
  has no count, the player range is ignored.
- **Next game** takes the first world in the shuffle order not played this round whose range fits the
  people now, and records it as played. A world with no range fits everyone.
- **Pick another** puts the shown world back and takes the next one after it in the order that fits.
- When nothing fits: **No world fits N people.** Nothing is recorded.
- It shows the world's picture, name and players, and a link to the world on vrchat.com. It never
  opens an instance.

## 7. Facts

| Fact | When | Actor |
|---|---|---|
| `modbot.world-list.create` | A list was made | the person |
| `modbot.world-list.change` | Renamed, or worlds or players changed; carries before and after | the person |
| `modbot.world-list.delete` | Deleted | the person |
| `modbot.calendar.world.pick` | A world was picked: `kind` is `date` or `game`; carries the list, the world, the date, the people count when one was used, and the world put back, if any | the person, or none when Modbot picked a date by itself |

All in the operational log. The subject of a list fact is the list's id; of a pick, the event's id.

**Purge is unaffected.** No table here holds anything about a member: lists of worlds, the shuffle,
and picks. The actor is a staff account, as on every calendar fact.

## 8. Later (not built)

- **Weight the picks by turnout**: worlds people stay for come round more often. The results (§13)
  already know how full each date's instance got.
- **Import a VRChat favourites list** into a world list. That reads VRChat's favourites, a new
  endpoint, so its rate limit has to be asked about first (§4.3.4).

## 9. Not checked against the real services

- Search VRChat on a real account: what `worlds.search` allows, and whether a name such as "Just B
  Club 4.0" finds the world. Localhost has no VRChat account, so only the can't-ask path was tried.
- Whether `GetWorld` answers 404 or 403 for a private world. Either is now refused and shown as
  VRChat's words (decided 2026-10-15), so a real private world cannot be added. Whether a typed name
  such as "Just B Club 4.0" gets 400, 403 or 404 is not known; all three are refused the same way.
