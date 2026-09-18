# Modbot — Everyone Modbot Has Seen

**Date:** 2026-09-18
**Status:** built
**Supersedes:** nothing. Narrows user profile sync design §3.1; adds a page to foundation §10.

---

## 1. What this is

Three changes that belong together because they are all about the same table, `vrchat_user`:

1. **A page for everybody Modbot has seen** — the People page, §2–§4.
2. **Stop refreshing people who came once and never came back** — §5.
3. **Sidebar and settings moves** — §6.

The first two are two halves of one observation. Modbot writes a row in `vrchat_user` for every
VRChat account it has ever seen anywhere, and then treats that table as if it were the member list:
it refreshed every row on a schedule as though everyone in it mattered equally, while showing none
of them anywhere except the ones who happened to be members. Both halves are now corrected, in
opposite directions — the table is *shown*, and it is *refreshed selectively*.

The maintainer's words, both on 2026-09-18:

> The subject page tries to load VRChat and Discord IDs. We need to be able to see all users, not
> just group members. Say users joined your instance, they kinda got tracked, so we need to be able
> to see a place with all users Modbot has interacted with, not just group members, so we can see
> their analytics and load them up and such.

> Users not in a group do not need to be regularly refreshed either. Say someone joined an instance
> once and never came back, we'd be wasting requests on them.

---

## 2. The name: People

The maintainer flagged the problem with the obvious name: **Users** is taken, by the settings screen
for Modbot's own staff accounts.

**People.** It is the word for the thing, it is a word a sixteen-year-old with no technical
background already knows, which is the test CLAUDE.md sets, and it collides with nothing — there is
no other "People" anywhere in the app or the API.

Rejected, with reasons, so the question is not reopened:

| Name | Why not |
|---|---|
| **Users** | Taken. Two screens called Users, meaning two unrelated things, is the worst of all outcomes. |
| **VRChat users** | Accurate and long. Every page in the sidebar is about VRChat unless it says Discord. |
| **Everyone** | Reads as a control, not a place. "Everyone" answers *who*, not *what is this page*. |
| **Profiles** | What one row leads to, not what the list is. A moderator looking for a person does not think "I will look in Profiles". |
| **Directory**, **Subjects**, **Identities** | Borrowed words for an ordinary thing, which is exactly what the naming rule rejects. |

The page's id, path and endpoint are all `people`: `/people` in the browser, `GET /api/people` on
the server.

---

## 3. What the page shows

One row per person, with what is useful at a glance and nothing that needs explaining:

| Column | What it is |
|---|---|
| **Person** | Picture and display name once the profile sync has fetched them, otherwise the id. The 18+ mark and the trust rank badge, as everywhere else. |
| **Standing** | **Member**, **Left**, **Banned**, or nothing. A person can be more than one of these; a person can be none of them, and most are. |
| **Last seen by Modbot** | The most recent thing Modbot recorded about them. |
| **Known for** | How long ago it first saw them — the answer to "is this somebody new". |
| **Profile** | How old the stored profile is, **Not fetched yet**, or **No such account**. |

Search matches the display name and the id, case-insensitively and literally, exactly as the member
list does — including the searchable form of the name, so decorated lettering is found by plain
letters (names design). The id is searched as text and never parsed; a legacy id follows no format
(foundation §3.1.1).

Filters are a membership picker (**Everyone**, **Members**, **Not members**, **People who left**)
and three sorts (most recently seen, by name, known longest). The endpoint also takes `banned` and
`profile=fetched|not-fetched`, which the page does not offer a control for yet and an API caller
can use today.

Clicking a row opens the person popup, the same one every other list in the app opens (foundation
§10.2). This page adds no way of looking at a person; it adds the list of which people there are.

Search, the filters and paging all run on the server. The member list gives the reason and this
page needs it more: `vrchat_user` outgrows the member list by an order of magnitude, because it
holds a row for every stranger anyone's companion has ever reported.

---

## 4. The permission

**See profiles (`ViewProfile`). No new permission.**

The question to answer before adding one was whether this page shows anything a person could not
already see. It does not: every field on a row is a field the person popup already shows to a
holder of See profiles, and the popup can already be opened from the audit log, the ban list, the
Flags page and the Live page. What the page adds is *which profiles exist* — a list where before
there was only a lookup.

See profiles rather than See members, because most of the list are not members and the page is not
about membership. Every built-in role that holds one holds the other (`BuiltInRoles`), so no
existing deployment sees a change in who can open what.

The counts above the list — how many people Modbot knows about, how many are in the group — are
part of the same answer and carry no separate gate.

---

## 5. Who the periodic refresh is for

The defect, in one sentence: the two periodic tiers of the refresh queue selected from every row in
`vrchat_user` with no membership condition at all, so somebody who walked through one instance a
year ago was queued for a refresh every six hours forever.

The rule, and the reasoning for it, are written where they belong — **user profile sync design
§3.1**, under *Who tiers 4 and 5 are for*. In short:

- A person enters tier 4 (**profile is old**) or tier 5 (**never refreshed**) only if they are a
  **current group member**, or were **last seen inside `RefreshNonMembersFor`** — 30 days, a
  setting (`userProfileRefreshNonMembersForSeconds`), carried by `UserProfileSyncOptions` beside
  the windows it already had and wired through the sync pacing document like the rest.
- Tiers 1–3 are untouched. A client reporting somebody in an instance, a moderator opening them in
  Modbot, and anything the fact log records about them all still queue a fetch at once, however
  long they have been gone.

**What an operator gives up.** The stored profile of somebody outside both groups goes stale and
stays stale. Opened a year later, the page shows the name and bio from the last time anybody cared,
with "last refreshed" saying how old that is, until the on-demand fetch that opening them starts
comes back a second later. Nothing is deleted, nothing becomes unreachable, and no history is lost.
The cost is paid by exactly the people nobody is looking at, and what is bought with it is that the
group's own members are refreshed out of a budget that is no longer being spent on strangers.

**Why not a shorter window than 30 days.** It has to be long enough that somebody who visits
monthly is never treated as a stranger, and short enough that a one-off visitor falls out of it
before their profile matters. Thirty days is the maintainer's month; it is a setting because the
right answer depends on how busy a group's public instances are, and a group whose instances are
mostly strangers may want it much shorter.

**Why the weekly user read is not narrowed too.** It is one request per person per week (about
0.017 req/s per 10,000 people), and it is the call that settles whether an account is really gone —
a public-profile 404 is only half that answer. Narrowing it would leave a 404 on somebody outside
the window permanently unsettled, which is a *wrong* answer rather than an old one. Recorded here
because it is the obvious next thing to do and the reason not to is not obvious.

---

## 6. The sidebar and the settings

Two moves the maintainer asked for on 2026-09-18.

### 6.1 Setup is now System

The group at the bottom of the sidebar holds **Logs** and **Settings**. Neither is setup; setup is
the wizard, which is a different thing at a different address, and a heading that names it invites
an operator to look for the wizard under it. **System** is what those two pages are about.

### 6.2 Users and Roles are now Settings' IAM tab

Both were sidebar pages, under **Team**, beside Reviews. They are not part of the daily work — they
are how the deployment is configured, read once when somebody is added and then not again for weeks
— and the sidebar is for the screens a moderator works in.

They are now the two halves of one Settings tab, built the way the API tab is built: a row of
sub-tabs under the settings tabs, at `#iam/users` and `#iam/roles`. The tab sits second, after Host
& Database, because both are about the deployment itself rather than about an integration.

**`/users` and `/roles` still work**, and land on the half they named (`MOVED` in `lib/nav.ts`).
The links exist in messages and in bookmarks, and an address that stops working reads as a feature
that was removed.

The two component files stayed in `src/pages/`. `CopyBox` is exported from `pages/Users` and
imported by five settings panels; moving the file would have been five import changes for no
behaviour, in a week when another agent is working in the same directory.

**Permissions.** Every other Settings tab asks for Manage settings; these two ask for Manage users
and Manage roles, which are deliberately separate (accounts and access design §3). So:

- the Settings nav entry asks for **any** of Manage settings, Manage users, Manage roles — otherwise
  somebody who may add a staff account but not reconfigure SMTP could not open the page the control
  now lives on;
- **Settings draws only the tabs the person may open**, and IAM draws only the half they may open;
- the server refuses the data regardless, as always (accounts and access design §8).

### 6.3 The name IAM was asked for, not chosen

**IAM stands for identity and access management.** It is borrowed jargon for an ordinary thing, and
a volunteer moderator would have to be taught it — which is precisely the test CLAUDE.md's naming
rule sets and precisely the kind of name it rejects. **People** and **Roles** as two tabs, or
**Accounts** as one, would satisfy the rule.

**The maintainer asked for IAM by name, directly, on 2026-09-18, and IAM is what was built.** This
paragraph exists so that a future reader who spots the violation knows it was seen, was raised, and
was overruled by the person whose deployment it is — and does not "fix" it.

---

## 7. Decision log

| Decision | Reason |
|---|---|
| A page for everyone in `vrchat_user`, not only members | The table was being written and refreshed but shown nowhere; the people in it have profiles, presence and history nobody could reach. |
| Called **People** | The plain word for the thing; **Users** is taken by the staff-accounts screen. |
| Gated on See profiles, no new permission | Every field on a row is already shown to that permission in the person popup; the page adds the list, not the contents. |
| Standing as member / left / banned, not one word | A person can be more than one, and most are none. |
| Search, filters and paging server-side | The table outgrows the member list by an order of magnitude. |
| Row opens the ordinary person popup | One way of looking at a person, from every list (foundation §10.2). |
| Tiers 4 and 5 narrowed to members and people seen in 30 days | A one-time visitor was queued forever out of the budget the group's members need. |
| The window is a setting, not a constant | How much of a group's traffic is strangers differs per deployment. |
| The weekly user read left alone | It settles whether an account is gone; narrowing it would leave 404s unsettled. |
| Setup renamed System | Setup is the wizard, at another address; Logs and Settings are the system. |
| Users and Roles into a Settings tab | Configuration, not daily work; the sidebar is for daily work. |
| Old addresses redirect | The links are already out there. |
| The tab called **IAM** | The maintainer asked for it by name. Recorded as an exception to the naming rule, not as an example of it. |

---

## 8. Not built, and open

- **No filter chips.** The People page uses plain controls rather than the chip bar the Members and
  Discord members pages use. Chips would want the page registered in `lib/pageFilters.ts`, which is
  shared, and the page has three controls. It is the obvious next step if the filters grow.
- **`banned` and `profile` have no control on the page**, only in the endpoint.
- **No counts per standing** beside the membership picker, the way the member list counts roles.
- **Nothing is refreshed from this page.** Opening a person from it refreshes them, through the
  ordinary on-demand tier; the list itself never queues anybody, which is deliberate given §5.
