# Modbot — Member profiles

- **Date:** 2026-10-12
- **Status:** Step 1 of 2 implemented with this document; step 2 (§8) added 2026-10-12
- **Covers:** the profile fields an admin sets up; what members fill in from Discord with `/profile`;
  who may see each field; where staff read a member's profile in Modbot
- **Step 2 (§8):** copying a member's existing roles into their profile, and a "Findable" switch
  for `/find` (`/find` itself comes later).
- **Related:** Discord commands design (every `/command` has a switch, §3.8); acting from Discord
  design (the staff account check); foundation §5.5 (a purge erases a person); ban reasons (the
  settings list this one copies the shape of)

---

## 1. What it is for

A community that hands out roles to say who people are -- pronouns, gender, orientation,
relationship status, time zone, hobbies -- ends up with dozens of roles that say nothing the
member could not say better in a sentence, and that everybody can see. A profile moves those out
of the role list. The admin decides which questions exist. Each member answers the ones they like
and decides who may read each answer.

## 2. Fields

An admin makes the fields on **Settings → Member profiles**. Each field has:

| Part | Rule |
|---|---|
| Name | 1 to 40 characters, not the same as another field's (letters compared without case). Discord's form label allows 45. |
| Kind | **Pick-list** (the admin sets the options) or **Free text** (up to 300 characters). Chosen when the field is added and not changed afterwards: a value of one kind means nothing in the other. To change it, remove the field and add a new one. |
| Options | Pick-list only. 1 to 25, each 1 to 60 characters, not repeating. 25 is what Discord shows in one select menu. |
| Who sees it by default | **Only me**, **Staff** or **Members**. A new field starts at **Staff**. Chosen when the field is added and not changed afterwards (see below). |
| Position | The order members see them in. Moved up and down; a new field goes last. |

At most 25 fields. Admins can add, rename, reorder and remove. **Remove** deletes the field and
every member's answer to it; the settings screen asks first, and the audit log records it. Removing
one option clears that option from the members who picked it (their choice of who sees the field
stays).

A field's default "who sees it" is fixed once the field is added. A member's own choice is stored
only when they make one, so until then their answer follows the field's default; if an admin could
widen it later (Staff to Members), answers members never chose to share would become visible
(user decision, 2026-10-12). Only the profile owner changes who sees an answer, in `/profile`. The
settings screen shows the default on an existing field as plain text, and the update call refuses
a different one with a 400 (the same level, or none, is accepted). To get a different default,
remove the field and add a new one.

All changes need **Manage settings** and are written to the audit log as a settings change (the
same entry every other settings screen writes).

## 3. Who sees what

| Level | Who |
|---|---|
| Only me | The member. |
| Staff | Modbot staff: a Modbot account that has proved its Discord account (the `/verifyaccount` code) and holds **See members**. Administrator counts. |
| Members | Everyone in the Discord server. |

**Staff can always see every field, whatever the member chose, including Only me.** The member's
"who sees what" screen does not change that, and does not say it (no explanatory text). A member
always sees all of their own.

The Modbot web page shows staff every answer the member gave and each one's level, so a moderator
knows what the member meant to be private.

## 4. In Discord: `/profile`

Registered like every other command: it has a switch (name `profile`) on Settings → Discord →
Commands, **off by default**, like the other commands for members. A switched-off command is not
registered, and its buttons and forms say it is off.

| You type | You get (only you can see it) |
|---|---|
| `/profile` | Your card: your name, one line per field with your answer, and the level beside it. Buttons **Edit**, **Who sees what**, **Findable: On/Off** (§8.2) and **Spotlight: On/Off** (the spotlight design, §2). |
| `/profile member:@someone` | Their card with only the fields you may see. No buttons. If they have none you may see: "{name} has nothing to show." |

Both look the same for a member with an empty profile: "{name} has nothing to show." for others;
for you, the card lists the fields with "Not set". With no fields set up at all the answer is
"No profile fields have been set up."

**Edit** and **Who sees what** open a form (Discord calls it a modal). A form holds at most five
boxes, so fields go in pages of five in the admin's order. With more than five fields the button
first answers with one button per page ("Fields 1 to 5", "Fields 6 to 10") and each of those opens
that page's form.

| Field | In the edit form |
|---|---|
| Pick-list | A select menu with the options, the current one marked. Left empty, it clears the answer. |
| Free text | A text box with the current words in it. Left empty, it clears the answer. |

The **Who sees what** form has one select menu per field: Only me, Staff, Members, with the
current level marked.

Nothing here is a Discord select menu inside a message: a message with several menus and the
buttons runs into Discord's limit of five rows, and the form keeps one pattern for every field.

After either form is sent the answer is the refreshed card.

The command and buttons are for **every member**: no Modbot account. Looking at somebody else
needs nothing either, but only a staff account (the check above) sees Staff and Only me fields.
Bots have no profile. A person Modbot has never seen in the server has none.

Nothing is written to the audit log for a member editing their own profile or for anybody looking:
the audit log is about moderation and settings, and a profile is the member's own words.

## 5. In Modbot

The Discord tab of a person's page shows a **Profile** panel to anyone who may see members:
each answer with its field name and the level the member gave it. Fields the member has not
answered are left out; a member with none shows "Nothing filled in."

## 6. Data

| Table | Holds |
|---|---|
| `member_profile_field` | id, name, kind, default level, position, created/updated |
| `member_profile_option` | id, field id, label, position, Discord role id (step 2) -- pick-list options |
| `member_profile` | Discord user id, Findable, updated (step 2); a row only while the member is findable or has opted in to the weekly spotlight (`spotlight`, the spotlight design §2) |
| `member_profile_value` | field id, Discord user id, option id **or** text, the member's own level (null = the field's default), updated. One row per field per member; deleted when it holds neither an answer nor a level |

The value table is keyed on the Discord user id as text (opaque, per foundation §3.1.1) with no
link to `discord_member`, so a member who leaves keeps their answers for when they come back, and a
purge deletes by id. Removing a field removes its options and values (cascade); removing an option
sets the value's option to null (the row stays only for the member's chosen level).

A **purge** of a Discord person (or of a VRChat person, for the Discord account linked to them)
deletes their values with the rest of what names them. The retention summary does not get a new
count.

## 7. Interface to the API

| Call | Needs | Does |
|---|---|---|
| `GET /api/settings/profile-fields` | Manage settings | The fields with their options |
| `POST /api/settings/profile-fields` | Manage settings | Add a field |
| `PUT /api/settings/profile-fields/{id}` | Manage settings | Rename, replace the option list (pick-list). A different default level is refused (400) |
| `PUT /api/settings/profile-fields/order` | Manage settings | Put the fields in this order |
| `DELETE /api/settings/profile-fields/{id}` | Manage settings | Remove the field and its answers |
| `GET /api/discord/members/{id}/profile` | See members | One member's answers, with levels |

## 8. Step 2: copy from roles, and Findable

### 8.1 Copy from roles

For a community that already hands out "about me" roles, an admin links each option to the role
it replaces and copies the roles into the profiles once. Discord roles are not touched: Modbot
never deletes or removes them, and the admin deletes the roles in Discord when they like.

- **Link.** On **Settings → Member profiles**, each option of a pick-list has a role picker (the
  same picker the other settings use, filled from Modbot's stored server roles). One role per
  option, and a role may be linked to only one option of a field. The link is the
  `discord_role_id` column of `member_profile_option` (null = none). A role may only be newly
  linked if it is one of the server's stored roles; a link already saved stays even if Discord
  later deletes the role, so saving a field never fails on it. A free-text field has no options,
  so only pick-lists take part.
- **Copy.** Each pick-list field has a **Copy from roles** button (off until an option is
  linked). It fills the field for every current member (not bots, not people who left) who holds a
  linked role and has no answer to the field yet.
  - An answer a member set is never overwritten. A member who only chose who sees the field has
    no answer yet: the answer is added and their level is kept.
  - A member who holds the roles of two options of one field gets the first option in the
    field's order.
  - A copied answer follows the field's default level (no level is stored for it).
  - It shows how many answers it filled, and running it again fills none that were filled
    before.
- **Audit log.** One settings change (setting `memberProfiles`) per press, naming the field and
  the count. A press that filled nothing writes nothing, as with every settings save. Linking
  roles to options is recorded with the field's other changes (`<field>: linked roles`).

| Call | Needs | Does |
|---|---|---|
| `POST /api/settings/profile-fields/{id}/copy-from-roles` | Manage settings | Copies as above; answers `{ filled }`. 400 for a free-text field |

The option list calls carry `roleId` on each option (null for none) and refuse an unknown role or
one used by two options (400).

### 8.2 Findable

A member decides whether `/find` may list them. **Off by default**, and it is the member's own
switch: staff and admins cannot turn it on for anyone.

- **In Discord.** The member's own `/profile` card has a third button, **Findable: On** or
  **Findable: Off**. Pressing it flips the switch and rewrites the card with the new label. It is
  the pressing member's own switch only. A fourth button, **Spotlight: On** or **Spotlight: Off**,
  works the same way for the weekly spotlight (the spotlight design, §2).
- **In Modbot.** The Profile panel on a person's Discord tab shows a **Findable** row (On or
  Off) to anyone who may see members. `GET /api/discord/members/{id}/profile` returns
  `findable`.
- **Stored** in `member_profile` (Discord user id, `findable`, updated). A member with no row is
  not findable; turning it off deletes the row when the Spotlight switch is also off, so nothing is
  kept for the default. The row stays while either switch is on, and turning one off leaves the other
  as it was.
- **Purge.** A purge of the Discord person (or of the VRChat person, for the linked Discord
  account) deletes the row with the member's answers.
- **`/find`** is not built here (see the find design). It lists only members whose switch is on, and it
  shows them only the fields their levels allow (§3); Findable decides whether they are listed at
  all, not which fields show.

## 9. What this does not do

- No answers are checked for content: free text is the member's own words. It is shown in
  Discord embeds and in Modbot as plain text, never as markup, and Discord mentions in it do not
  ping (the card is private anyway).
- Staff viewing a profile is not logged.
- A member cannot add their own fields or options.
- Roles are copied only when an admin presses Copy from roles (§8.1); nothing follows a member's
  roles afterwards.
