# The Members and Bans pages

Modbot keeps its own copy of your group's member list and ban list, read from VRChat a page at a
time. The Members page and the Bans page show those copies. Nothing on either page is typed in by
hand or guessed; if Modbot does not know something yet, the page says so.

## Members

The Members page lists everyone in the group as of the last full read of the list, newest joiner
first. Each row shows the person's picture and name once Modbot has fetched their profile — until
then it shows their VRChat id — along with their roles, when they joined, an **18+** mark for
anyone Modbot has ever seen as 18+ verified, and when Modbot last saw them do anything.

You can search by name or id, filter by role, and switch between current members, people who
have left, or both. Clicking a person opens their profile pane, which now also shows their
membership: member since, roles, whether they are representing the group, and whether they are on
the ban list.

The line above the list says **when it was last synced**. Modbot reads the whole member list,
one page every two seconds, then rests for fifteen minutes and reads it again — so a change on
VRChat's side shows up here within about a quarter of an hour. When Modbot is reading the list for
the very first time the page says so plainly: what you see until then is however many pages have
come in, not the whole group.

The bot's own account is never in the list VRChat returns, so it does not appear here.

## Bans

The Bans page has two tabs, because there are two different questions.

**Ban list** is the group's ban list: everyone VRChat says is banned right now, whenever the ban
was issued. This is the one to check before concluding that somebody is not banned. It is read the
same way as the member list, one page every few seconds and a full pass every half hour, and it
says when it was last synced.

**What the audit log recorded** is Modbot's memory of the bans it watched happen, with who issued
each one. VRChat's audit log only reaches back about a month, and Modbot only started reading it
when it was installed, so this tab does not go back further than that — it says so at the top,
every time. But it is the only one of the two that knows *who* banned somebody.

## Why somebody was banned

Neither list knows. VRChat's audit log has nowhere to put a reason, so the group has to write one
itself — see [Case files](case-files.md). Both lists show whether a ban has one, and the card above
them counts the ones nobody has written up yet.

## How changes are recorded

When somebody joins, leaves, gets a role, or is banned or unbanned, Modbot records it in the
audit log timeline. Most of the time that record comes from VRChat's own audit log, which says
exactly who did it and when. The member and ban lists are a backstop: if the audit log missed
something, the list will show the difference on the next read, and Modbot records it then — marked
as inferred from the list, with no "who", because a list cannot say. It never records the same
event twice.
