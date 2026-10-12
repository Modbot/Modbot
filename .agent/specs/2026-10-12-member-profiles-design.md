# Modbot — Member profiles

- **Date:** 2026-10-12
- **Status:** Step 1 of 2 implemented with this document
- **Covers:** the profile fields an admin sets up; what members fill in from Discord with `/profile`;
  who may see each field; where staff read a member's profile in Modbot
- **Not in this document (step 2):** copying a member's existing roles into their profile, and a
  "Findable" switch for `/find`. §8 says how the data leaves room for them.
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
| Who sees it by default | **Only me**, **Staff** or **Members**. A new field starts at **Staff**. |
| Position | The order members see them in. Moved up and down; a new field goes last. |

At most 25 fields. Admins can add, rename, reorder and remove. **Remove** deletes the field and
every member's answer to it; the settings screen asks first, and the audit log records it. Removing
one option clears that option from the members who picked it (their choice of who sees the field
stays).

Changing a field's default "who sees it" changes it for every member who has not chosen for
themselves, because a member's own choice is stored only when they make one.

All changes need **Manage settings** and are written to the audit log as a settings change (the
same entry every other settings screen writes).

## 3. Who sees what

| Level | Who |
|---|---|
| Only me | The member. |
| Staff | Modbot staff: a Modbot account that has proved its Discord account (the `/verify` code) and holds **See members**. Administrator counts. |
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
| `/profile` | Your card: your name, one line per field with your answer, and the level beside it. Buttons **Edit** and **Who sees what**. |
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
| `member_profile_option` | id, field id, label, position -- pick-list options |
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
| `PUT /api/settings/profile-fields/{id}` | Manage settings | Rename, set the default level, replace the option list (pick-list) |
| `PUT /api/settings/profile-fields/order` | Manage settings | Put the fields in this order |
| `DELETE /api/settings/profile-fields/{id}` | Manage settings | Remove the field and its answers |
| `GET /api/discord/members/{id}/profile` | See members | One member's answers, with levels |

## 8. Room for step 2

- **Roles to options.** An option has its own id that never changes when it is renamed or moved.
  Step 2 adds a table that pairs a Discord role id with an option id; a pass reads each member's
  roles and writes the paired options into `member_profile_value` for members who have not
  answered themselves. Nothing in step 1 has to change for that. A free-text field has no option
  to pair, so only pick-lists take part.
- **Findable.** One more column per member (or per field) read by `/find`; the level scheme
  already says who may see an answer.

## 9. What this does not do

- No answers are checked for content: free text is the member's own words. It is shown in
  Discord embeds and in Modbot as plain text, never as markup, and Discord mentions in it do not
  ping (the card is private anyway).
- Staff viewing a profile is not logged.
- A member cannot add their own fields or options.
- No import of the old role names; that is step 2.
