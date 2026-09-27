# An instance against its world

**Date:** 2026-09-27
**Owner:** Zuelatak
**Status:** Built. What VRChat's list holds has not yet been checked against a live response.

## What was asked

> "When I look at an Instance I'd love to see how it compared to other instances at the time."

This is the website's instance pop-up. "Other instances" first read as the group's own instances.
The owner corrected that: they meant instances **outside the group**, and of the three readings
offered (the same world, other groups' events, all of VRChat) chose **the same world**. That means
"your Murder 4 lobby against the other Murder 4 instances running then".

## Where the numbers come from

Modbot kept nothing about instances outside the group. The group's own head counts
(`instance_head_count`) cover the group's instances only. The world page was read once, when a world
was first seen, and its counts were thrown away. Presence covers only where a moderator's client
stood. Modbot Cloud's public feed carries no count, by design, because it feeds a public page.

VRChat's world page (`GET /worlds/{worldId}`, `worlds.read`) carries `occupants`, `publicOccupants`,
`privateOccupants`, and `instances`: a list of `[instanceId, headCount]` pairs. That list is the
source.

**Rate limit (foundation 4.3.4).** Asked of the owner on 2026-09-27, before any new use. The
agreement: one read per world every **two minutes**, only for worlds the group has an instance open
in, only while it does. `worlds.read` was measured at one per second and is shared with the world
sweep. Three worlds open is 1.5 requests a minute. A 429 stops the pass cold and is not retried.

**Not probed.** A one-off probe of a live world page was offered to find out which instances the list
carries (public only, or group-public too) and whether the group's instance appears with its own
page's count. The probe tool had no sign-in, and the owner chose to skip it: "build it and save
what VRChat sends". Hence:

- `world_head_count` keeps the page's `instances` array **exactly as sent**, as `jsonb`, and the
  three occupant counts read from the body (null when missing, never nought).
- The list is taken apart when a pop-up asks (`InstanceWorldQuery`), so a wrong guess about its
  contents costs a query change, not lost data.
- If the list does not carry the group's instance, its own head count at that moment stands in for
  ranking, and the read says `listed: false`. When the list does carry it, the list's number is
  used, so the rank compares like with like.

Readings begin on the day this is deployed. Earlier instances have none, and the panel says so and
nothing more.

## What the pop-up shows

Under "People over time" on the Overview:

- **At its peak**: its rank by head count at the read where it held the most: "1st of 42".
- **Busiest for**: how long it held more people than every other instance in the world. Each read
  counts until the next one, and never for more than five minutes, so a gap in the reads is not
  counted.
- **In the world at its peak**: `occupants` at that read.
- **Other instances**: how many the list carried at any read.
- A chart: this instance as one bold line, the eight busiest others as quiet ones, at each read.
- **Busiest others**: those eight, with who can join (from the id's qualifiers), most at once, and
  how long they were seen for. One Modbot has a row for opens its own pop-up.

## Size

A row per read, not per change, because the list moves on nearly every read. A busy world lists
perhaps a hundred instances, about 2 KB a read, so roughly 30 reads an hour for each world with a group
instance open. No retention rule was added. If the table grows past what is wanted, it is the
obvious candidate for one.

## Still open

- Check a saved row after the first real evening: does the list carry group-public instances, and
  does the group's instance's number there match its own page's `userCount`?
