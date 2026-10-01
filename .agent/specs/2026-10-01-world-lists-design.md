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
the world is then kept as its id and the sweep names it on its next pass. VRChat's 404 is shown as
"VRChat has no world with that id." The id itself is never checked for shape: a link is taken apart
only to find the id in it (`worldId=` or the part after `/world/`), and anything else is taken as the
id as typed.

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
- An empty list picks nothing; the event has no world until the list has one.

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

- Whether `GetWorld` answers 404 or 403 for a private world. Either is shown as VRChat's words.
