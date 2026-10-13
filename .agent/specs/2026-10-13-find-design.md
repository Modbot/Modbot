# Modbot — /find

- **Date:** 2026-10-13
- **Status:** Implemented with this document
- **Covers:** `/find` in Discord: a member looks for other members by what their profiles say, such as
  "singers in UTC-5"
- **Related:** member profiles design (§3 who sees what, §8.2 Findable); Discord commands design (§3.8
  every command has a switch)

---

## 1. What it is for

Profiles say who people are. `/find` turns that around: instead of opening one card, a member asks
for the members whose profiles say something. It must not become a way to read what a member kept
private, and it must not list anyone who did not ask to be found.

## 2. Decisions

| Question | Answer |
|---|---|
| Who may search | Any member of the server. No Modbot account. |
| Who shows up | Only members who turned **Findable** on (profiles design §8.2). Never the searcher, a bot or a member who left. |
| What the searcher gets | A reply only they see. Names, and a **View profile** button for each. |

## 3. The command

`/find field: value: also-field: also-value:`

- `field` and `value` are required. `also-field` and `also-value` are a second pair, both or neither.
  A member must match every pair.
- `field` is suggested from the fields the admin made. A name that is typed in full, in any letter
  case, is taken as it is; otherwise the one field whose name holds the words.
- `value` depends on the field. A **pick-list** suggests the options' labels. A label typed in full,
  in any letter case, matches that option only; other words match every option whose label holds
  them. A **free text** field suggests nothing, and its answers match when they hold the words, in any
  letter case. The words are looked for as typed: `%` and `_` mean themselves.
- Suggestions name no member, so they are not recorded and are offered to anyone while the command is on.

## 4. Who a search can see

A member matches a field only through an answer **the searcher may read**:

| Searcher | Answers that can match |
|---|---|
| Any member | Answers the member opened to **Members** (their own choice, else the field's default) |
| Staff (a proven Modbot account that may see members, as `/profile` decides) | Every answered field, whatever level the member chose |

A hidden answer therefore never matches, and "nobody has that answer", "their answer is hidden" and
"they are not findable" all produce the same reply. Findable decides whether a member is listed at
all, for staff too; it does not decide which answers a searcher may read.

## 5. The reply

- Private. A card titled **Found N** listing up to **25** members by name, in name order, numbered. If
  there are more, a last line says how many ("and 2 more").
- One **View profile** button for each member listed, labelled with the member's number when there
  are several (Discord buttons carry no member name beside them). A press shows that member's card
  exactly as `/profile member:` would show it to the one who pressed, so the same levels apply; it
  follows the `/find` switch.
- No match: "Nobody found."
- A name that is not a field, or a pair with only one half filled in, is told so.

## 6. Switch, limit, record

- **Off by default** on the Commands card, like `/profile`. A switched-off command is not registered.
- **Per-person limit:** the same as `/remindme` (5 a minute, in memory); a refused call is not recorded.
  Pressing View profile is not counted: it shows what `/profile` shows, which has no limit.
- **Record:** a search is recorded as **Discord command used** like every other command, with the
  command name and the outcome. The words searched for and the members found are not stored.

## 7. What this does not do

- No search by name, role, join date or message count; only profile fields.
- No "or" between conditions, no more than two conditions.
- No paging: past 25, the searcher narrows the search.
- No way to find a member who did not turn Findable on, for anyone.
