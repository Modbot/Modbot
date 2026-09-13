# Reviews and repeat offenders

Modbot reads the group's audit log, so it already knows every kick, warn, ban and removal any
moderator has done in VRChat. Two screens are built on that record. Neither one does anything to
anybody — they count, and they ask.

## People acted on more than once

On the **Bans** page, the second tab lists everyone who has been acted on more than once: kicked from
an instance, warned, banned, removed from the group, or had a join request turned away — by any
moderator, in any instance. The most recent action is at the top.

For each person you see how many actions in total, how many in the last 30 days, the count by kind,
how many different moderators acted, and the last action with who did it. Clicking a name opens
their pane.

Each person has a status, and the rule that decides it is printed above the table:

- **Once** — one action, ever.
- **More than once** — two or more, but not recently enough to count as repeat.
- **Repeat** — three or more actions in the last 30 days. (The number is a setting; the page shows
  whatever it currently is.)

Unbans are shown but never counted as an action against somebody. Lifting a ban is relief, not
another strike.

The same numbers appear as a **History** block on any person's pane, under their profile: *acted on
4 times by 3 different moderators, last kicked from an instance 2 days ago by Alice*. If nobody has
ever acted on them, it says so.

The counts are rebuilt from the recorded history every fifteen minutes, and each screen says when
that last happened. They cover what Modbot has recorded — the same window as the audit log — and
nothing before it.

## Reviews

The **Reviews** page, under Team, is for people who hold the *Review tickets* permission. It lists the
moments when a moderator's recent pattern looked unusual enough that somebody should look.

A review is a question, not a finding. Modbot never judges a moderator, never restricts one, and
never messages one. It writes a plain sentence with the numbers in it and waits for a person.

There are two things it looks for:

**Keeps acting on one person.** One moderator has acted on the same person three or more times in
30 days, across at least two different instances or days, and no other moderator has ever acted on
that person. If other moderators *have* acted on the same person — the persistent troll everybody
removes — the bar is much higher: eight or more.

**Far more actions than the rest of the team.** One moderator did ten or more actions in a day, and
that is at least four times both what the next busiest moderator did that day and what the team
usually does per moderator per day. Comparing against the next busiest is what keeps a raid night,
when everybody is busy, from opening a review on whoever happened to be busiest. Modbot waits until
the team has a week of history before running this check at all, because on day one there is no
"usual".

"Usual" is each moderator's actions over the last 90 days, up to yesterday, divided by the days they
did anything. Today is left out on purpose, so a busy day cannot raise the usual it is compared to.

Every review shows the sentence, the numbers under it — actions, kind, places, other moderators, the
team's usual and theirs — the exact rule it was held against, and the ids of the facts behind the
numbers. If you disagree with the question, you can see exactly how it came to be asked.

### Closing a review

Write a short note saying what you concluded — *"asked her; a crasher kept coming back under new
accounts"* — and close it. The note is required. It is kept with the review and recorded in the audit
log against your account, because "this was looked at and found fine" is worth keeping as much as the
pattern was.

A closed review is never reopened by the same actions. If the same pattern happens *again* — new
actions after the close — a new review opens about those. While a review is open, its numbers are
kept up to date; it does not multiply.

The number beside **Reviews** in the sidebar is how many are waiting.

## What these screens cannot see yet

Moderation done *through* Modbot arrives later (M4). Until then, everything here comes from actions
done in VRChat directly, and VRChat's audit log attributes anything Modbot itself does to Modbot's own
account. When Modbot starts performing actions, the reason a moderator chose — the one-tap
classification — will also feed the reviews: a pattern fully explained by consistent reasons that
other moderators corroborate will close itself without being shown.
