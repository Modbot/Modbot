# Member profiles and the "18+ verified" flag

Modbot fetches the public VRChat profile of everyone it sees — everyone who joins, leaves, is banned,
is kicked, appears in an instance a moderator's client is watching, or does any of those things to
somebody else. The profile is what VRChat shows anybody who looks the person up: display name, bio,
status line, pronouns, avatar picture, join date, trust tags, and whether they show themselves as
18+ verified.

## Profiles are fetched one at a time, so they have an age

VRChat hands profiles out one person per request. Modbot asks at up to 3.5 requests a second and
spends that on the people who matter most first: anyone in an instance right now, then anyone a
moderator has open on screen, then anyone who just did something, then the oldest profiles, then
people it has never fetched at all. A typical group of 8,000 is refreshed in about 40 minutes; the
largest groups take hours.

That is why every profile in Modbot says **when it was last refreshed**. A bio from an hour ago and a
bio from last week are different claims, and the screen never shows one without saying which it is.
A profile older than six hours is labelled stale. Opening a person's profile asks for a fresh copy
and shows "refreshing…" until it arrives; if it cannot arrive — VRChat is rate limiting Modbot, or
the account no longer exists — the screen says so in plain words and keeps showing what it has.

## "18+ verified" never turns itself off

VRChat lets people choose whether their age verification is visible. Somebody who showed "18+"
yesterday can hide it today, and to anyone looking at their profile they then appear unverified.

Modbot treats verification as something it has **observed**, not something it re-checks. The moment
any refresh sees a person as 18+ verified, Modbot's flag for them is set, and **no refresh ever
clears it**. Hiding the badge changes what VRChat shows; it does not change the fact that the person
verified.

The profile shows both things side by side: Modbot's flag, with when it was first seen, and what
VRChat shows right now. When they disagree, the screen says so.

The only way the flag is cleared is by a moderator who holds the **Edit age verification**
permission, on purpose, with a reason. That action — and any moderator setting the flag by hand —
is recorded in the audit log against the account that did it. If VRChat later shows the person as
18+ verified again, the flag is set again: a new sighting is new evidence.

## What Modbot does not keep

Modbot stores the profile as VRChat returned it, minus three things: the person's current instance
location (which can contain an instance secret), the private note your VRChat account may have on
them, and the friend key. Nothing else is filtered; a question nobody has asked yet can usually be
answered from the stored copy without another request.

A person whose account VRChat no longer knows is kept, marked as not found, with the last profile
Modbot saw. Their history still mentions them, and deleting the row would not undo that.
