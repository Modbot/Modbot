# Case files

VRChat's audit log records that somebody was banned, who did it, and when. It does not record
**why** — there is nowhere in it for a reason to go.

A case file is the group's own account of a ban: the reasons, what happened in the moderator's own
words, the screenshots or video they kept, and a copy of the person's profile as it looked at the
time. It is what you will want three months later, when the ban is questioned, or when the
moderator who issued it has moved on.

## Writing one

Go to **Bans**. Every ban on both lists has a case file column.

- **Write the case file** — nobody has written one for this ban yet.
- **Open the case file** — somebody has.

At the top of the page there is also a card counting the bans in the last 30 days that nobody has
written up, with a button beside each.

Writing one asks for two things:

**The reasons.** Tap as many as fit. This takes a second and it is the part the accountability
checks actually read — they can tell "five actions, all Harassment, three other moderators agree"
from "five actions, all Other, nobody else has ever touched this person", and they cannot tell
anything at all without it.

**What happened, in your words.** Optional for most reasons, required if you pick **Other** —
"other" on its own says nothing. Markdown works: `**bold**`, lists, `> quotes`, headings.

Press **Write the case file** and the page opens.

## Attaching evidence

On the case file page, under **Evidence**, press **Attach a screenshot or video**. The accepted
formats and the size limit are printed next to the button; they are set in Settings → Evidence by
whoever runs this Modbot.

You will see the progress as it uploads, with the rate. A big clip on a home connection takes
minutes, and you can cancel and retry — nothing is attached until the upload finishes, so a
cancelled one leaves nothing half-done on the case.

Images show inline; video plays in the page and can be seeked. Everything has a **Download** link
and the fingerprint (`sha256`) of the file underneath it, which is what proves the file served is
byte-for-byte the file that was stored.

**To put a screenshot inside your written reason**, copy the `sha256` under it and write:

```
![what it shows](evidence:the-sha256-you-copied)
```

Only evidence attached to the same case file can be shown this way. Image links to anywhere else
are never loaded — an image on someone else's server would tell them the address of every
moderator who opened the case, and the time they read it.

Attaching needs the **Upload evidence** permission. Seeing what is already attached needs **View
evidence**, which is separate on purpose: knowing somebody was banned for harassment is not the
same as needing to watch it happen.

## The profile at the time

Modbot copies the person's profile — display name, bio, status, pronouns, tags, whether they had
shown as 18+ verified — into the case file the moment you write it, along with their group roles
and their entry on the ban list.

This is the part people forget to keep. A banned account changes its name and clears its bio within
the hour, and the screenshot somebody meant to take was never taken.

The page always says **when** the copy was taken and how old the profile already was then. That
matters: Modbot refreshes profiles on a schedule, so the copy might have been a few hours old when
you wrote the case file.

When you write one, Modbot also asks VRChat for a fresher profile. If one arrives, the page offers
**Refresh and capture again** — you can use it **once**, and what was there before is kept in the
record either way. After that the snapshot is fixed.

The profile picture is shown from VRChat's own servers, so it may stop loading if the account is
deleted. Modbot does not keep a copy of the image itself.

## Editing and withdrawing

The person who wrote a case file can edit it, and so can anybody who can ban. Every edit is
recorded against your account, with what it changed — the case file says what it says now, and the
history says everything it has ever said.

**Case files are never deleted.** If one should not have been written — the wrong person, the wrong
ban — **withdraw** it with a note saying why. It stays readable, its ban counts as unwritten again,
and a new one can be written. What nobody can do is make it look as though the group never wrote
anything.

## The reason list

Settings → **Moderation** holds the list of reasons. It starts as:

Harassment · Hate speech · Crashing or malicious avatars · Underage · Ban evasion · Spam · Other

Anybody with the **Edit the reason list** permission can add to it, reword an entry, reorder it, or
switch one off. A switched-off reason disappears from the buttons and stays on the case files that
already picked it.

There is no delete, and that is deliberate: case files name the reason they were given, so deleting
one would take that classification off every case file that used it.

## Who can do what

| | Permission |
|---|---|
| Read case files | See profiles |
| Write one | Ban |
| Edit or withdraw one | Ban, or being the person who wrote it |
| See the evidence on one | View evidence |
| Attach evidence | Upload evidence |
| Change the reason list | Edit the reason list |

## What the list of unwritten bans does and does not cover

The count is taken from the bans Modbot **watched happen** in the audit log. It starts when this
deployment first synced and reaches back only as far as VRChat's own audit-log history still went
at that moment.

So an empty card means every ban Modbot recorded has been written up. It does not mean every ban
the group has ever issued has been.
