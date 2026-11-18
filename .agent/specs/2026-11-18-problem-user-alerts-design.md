# Problem user alerts

**2026-11-18**

Staff should be warned about problem users in an instance before they cause harm. Two alerts share
one path: a person wearing an avatar on a list the team keeps (this document's first half, built),
and a crash alert for an instance where many people left at once (the second half, step E, not yet
built).

Builds on the flagged join alert (flagged rules design) and the companion's alert hub; no new
transport.

## 1. Avatar list

### What is stored

A **problem avatar** is a name, who added it, and when. A second column holds the name's **key**:
trimmed and lower-cased. The key is unique, so marking an avatar twice is one entry.

### Name only

VRChat's log gives an avatar's name and never its id, so the list can only match names. Two avatars
with one name are one entry, and a renamed avatar falls off the list. This is the cost of the
source, not a choice, and the docs page says so.

- Matching compares keys: trimmed and case-insensitive.
- **Mark avatar** on a person stores the **exact** name as the person's latest avatar fact carried
  it, not the key, so the settings list reads the way VRChat does.
- The list is changed under the permission that manages flag rules (ManageSettings). Reading the
  person's avatar for the button needs the same permission.

### Where it is marked and read

- The person's Overview shows the avatar they were last seen wearing, with **Mark avatar**, or
  **Problem avatar** when it is already listed. One action, no dialog.
- Settings, Moderation, **Problem avatars** lists the names, each with **Remove**.
- No explanatory text in either place; the docs page carries it.

### When an alert is raised

A fact of type avatar changed, instance joined or presence observed, newly written (not a repeat of
a fact already stored), in an instance, carrying an avatar name that matches the list.

- The alert is the flagged join alert with the reason "Wearing a problem avatar: <name>". Same card,
  same bleep, same hub.
- **The reporter's own companion is alerted too.** The reporting device is passed as `Guid.Empty`,
  so the hub's "not the reporter" filter drops nobody. A flagged join does not need this because the
  reporter's own client already knows the rules; nothing in a client knows this list.
  *(Builder's choice, not in the approved plan.)*
- Every paired, unrevoked device is a recipient, as with a flagged join.

### Dedupe

The fact log already drops one report of one event. Two moderators seeing the same person in the
same avatar are two events, so a second, in-memory table keyed **person + avatar key + world +
instance** stops the second alert. Entries are pruned after about 12 hours.

- It is in memory on purpose: no table, no migration for something that is only a noise guard.
- A restart empties it, so somebody still in that avatar can be alerted once more. Accepted.
  *(Builder's choice, not in the approved plan.)*
- The same person returning to the same avatar in the same instance within the window is not
  alerted again.

### Discord

When the **Post in Discord** setting under Flagged is on, each hit is posted as one line to the
chosen channel, the same way a flagged join is: "Flagged: <name> is wearing a problem avatar in a
group instance: <avatar>". A failed post is logged and never stops the headset card, which has
already gone out.

### What the companion card says

The card is the flagged join card, whose title reads "joined". For an avatar switch by somebody
already there that is slightly wrong. Step D (companion card wording) may refine it; this step
leaves it alone.

## 2. Crash alert (step E)

Not built here. Recorded so the avatar list and the crash alert share one shape.

### Signal

A **possible crash** is when many people leave one instance within a few seconds, as seen in a
paired companion's own log. The companion already reports each leave as a fact with its time and
the instance, so the server counts leaves per instance in a sliding window. A companion's own log
is the only source: Modbot has no other view of an instance.

### Settings, not constants

Both numbers are settings with defaults, and they are read each time, never hardcoded. VRChat's
capacity limits differ by group and are raised by exemptions, so the count cannot be a fixed share
of a fixed size.

| Setting | Meaning | Default |
|---|---|---|
| Leaves | How many people must leave | 5 |
| Seconds | Within how many seconds | 5 |

Defaults are a starting point to be tuned against real instances. Time comes from `IModbotClock`.

### What the alert says

- A **Possible crash** card on the companion, naming the instance.
- It names the people who **changed avatar or joined just before** the leaves, newest first, from
  the same fact log. That is the likely cause, so it is the thing a moderator wants to see.
- One tap opens each named person (the same popup as everywhere), so a moderator can mark an
  avatar or act without searching.
- When **Post in Discord** is on, one line is posted per alert, in the same channel and the same
  way as the avatar alert.

### One alert per event

One crash is one alert: after raising, the instance is quiet for the window's length, so a slow
trickle of leaves after the crash does not raise it again. Like the avatar dedupe this is in
memory.

### Not decided

- How far back "just before" reaches (about the same number of seconds again is the start).
- Whether a crash alert should link to the **Copy crash details** text the companion already makes.
