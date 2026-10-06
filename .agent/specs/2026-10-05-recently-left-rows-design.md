# Recently left rows design

- **Date:** 2026-10-05
- **Status:** Built on `staging`. Compiled; not yet tried in a headset or on the desktop overlay.
- **Covers:** the Instance list on the headset panel and the desktop overlay window
- **Builds on:** overlay list filters (`2026-09-26-overlay-list-filters-design.md`)

---

## 1. What it is

When somebody leaves, their row stays on the Instance list for 60 seconds, greyed, with a "Left" tag
and the seconds still to go ("42s") where their join time was. Tapping it still opens their card.
After 60 seconds the row goes. If they come back inside the minute, their ordinary row is back and
the greyed one is gone: nobody is listed twice.

"N here" counts only the people present. With a filter on, it says "1 of 5 here", counting present
people in both numbers.

## 2. Why

A row that vanishes the moment somebody leaves gives a moderator no time to tap it: the person they
wanted to look at is gone before the controller arrives.

## 3. How somebody counts as having left

Each roster read for the instance is compared with the one before. Whoever was on the earlier one
and is not on the later one has left, as of the moment of the later read. The first read for an
instance only sets what to compare with, so walking in never shows the room as just left. A leave
makes the next roster read due at once, so the greyed row appears within a tick of the leave.

The minute is counted on `IModbotClock`, never the system clock. It is held in memory and goes when
the moderator walks into another instance or a pairing is removed. Nothing new is asked of any
server and nothing is sent.

## 4. What the row looks like

The row is the person's own row, faded to half, with the name in the dim text colour, so it follows
whichever look the panel is in: the palette selected in VRChat on the desktop window, the headset's
own on a headset. The "Left" tag and the seconds are the only new words and stay at full strength.
There is no "+" for a heads-up on it: that is placed on somebody present.

## 5. Order and filters

Rows of people who left are sorted and filtered together with everybody else, so a row stays where it
was while the order allows it. For Newest first, the arrival time the log gave while they were here is
kept for them, because the log lets go of a person who has left.

## 6. Drawing

The panel is drawn again each time the countdown moves a whole second, but only while the Instance
list is showing and somebody's row is counting down.

## 7. Decisions

| Choice | Reason |
|---|---|
| 60 seconds | Long enough to reach for a row; short enough that the list is not a history. |
| Seconds, not "1m" | The user asked for a plain countdown. |
| Row kept as it last was | The server's roster has already dropped them, so the last row is all that is known. |
| No "+" on the row | A heads-up is placed on somebody present. Revisit if moderators want Keep an eye on somebody who just left. |
