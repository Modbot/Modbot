# Feedback: staff speak a bug, an idea or a change

**Status:** all 5 steps built (the server, the web page, the companion's main window and desktop overlay, the headset panel, the chat tool). Approved 2026-11-18.
**Builds on:** the Dictate design (2026-11-05), whose microphone, speech model and download the
button reuses; the AI chat design §3.3, which makes a tool that changes something an acting tool.

---

## 1. What was asked for

A **Feedback** button with a microphone, next to Dictate: tap, speak, tap to stop. Speech becomes
text on the member's own PC, the way Dictate does it. A draft shows a kind (Bug, Idea, Change), a
title and details, all editable, with Cancel and Send. Nothing is saved without Send. Admins read
what was sent on a **Feedback** page in the web app and mark it.

**Where it lands: this server, and only this server.** The words are stored in this Modbot's own
database. Nothing is sent to Modbot's own cloud service, to GitHub or to any other site. If the
admins want something fixed they read it here and decide for themselves.

## 2. The steps

| Step | What | State |
|---|---|---|
| 1 | The server: the table, the two permissions, the endpoints, the companion's way to ask whether it may send | built |
| 2 | The web app: the Feedback page, its menu entry, its link preview, its docs page | built |
| 3 | The companion's main window and the desktop overlay: the button and the draft window | built |
| 4 | The headset panel: the button and the draft panel (lockable, click-through, grabbable, saved spot) | built |
| 5 | The chat tool `file_feedback` and the chat's nudge to use it | built |

## 3. What is stored

One row per item in the table `feedback` (`FeedbackItem`):

| Field | Meaning |
|---|---|
| id | A version 7 id |
| sender | The sender's account id and the username they had then. No foreign key, so the feedback and the name outlive the account |
| kind | `bug`, `idea` or `change` |
| title | Up to 120 characters |
| details | Up to 4000 characters |
| status | `new`, `reviewed`, `done` or `dropped`. Starts `new` |
| created at | When it was sent (the clock, never the system time) |
| status changed at, by | Null while it is still new |

An index on status and date serves the list. What is saved is what the sender pressed Send on.
When AI tidied the words it was asked to take out the names of people, worlds and groups, but the
sender can edit the draft, so what is kept is theirs.

## 4. Tidying with AI, and with AI off

The companion sends the spoken words to the server, and the server answers with a draft.

- **AI on** (an AI provider is set up, as the briefs need): one Chat call, recorded and limited
  under `chat` like every other call Chat makes (the spend limits, the person's limits and the
  fallback model apply). The model is asked for a kind, a title of at most 80 characters and the
  details, with the names of people, worlds and groups replaced by "a member", "a world", "a group".
  The answer is read leniently.
- **AI off, over a limit, down, or an answer nobody can read:** the draft is the spoken words as
  the details, no kind and no title, and `tidied` is false. The sender picks the kind. **Tidying
  never blocks a send.**

Tidying does not depend on the AI chat switch or on the Use AI chat permission: it is a small
helper for the draft, not a chat. Nothing is saved by it.

## 5. Permissions

| Permission | Bit | Who holds it | What it opens |
|---|---|---|---|
| Send feedback | 61 | Moderator (built in, given by the migration) and Administrator | The button, tidy and send |
| Review feedback | 34 | Administrator only. Not in a built-in role, so an operator who wants a role to read feedback gives it | The Feedback page: list, open, change status |

Bit 62 is Administrator and 63 the sign bit, so Send feedback takes the highest free bit below them,
as the report permissions did. Bit 34 is free on every branch. An operator's own roles are never
changed by the migration.

The button's availability follows Send feedback and nothing else: not AI chat, not Use AI chat and
not a linked VRChat account.

## 6. Endpoints

**Companion (device token, as the device's owner)**, under `/api/v{version}/companion`:

| Request | Answer |
|---|---|
| `GET /feedback` | `{available, ai}`. `available` is whether the owner holds Send feedback. `ai` is whether the words can be tidied. The client shows the button when `available` is true |
| `POST /feedback/tidy` `{text}` | `{kind, title, details, tidied}`; `kind` and `title` may be null |
| `POST /feedback` `{kind, title, details}` | `{id}`. Saved as the owner |

Without Send feedback the last two answer 403 with the code `feedback_not_allowed`. A kind that is
not one of the three, empty details, a title over 120 or details over 4000 characters answer 400. A
title left empty is the first line of the details. A device token reaches nothing else of Feedback:
it cannot list or open anything.

**The web app (signed in), under `/api/feedback`**, all needing Review feedback (403 otherwise):

| Request | Answer |
|---|---|
| `GET /` (optional `status`) | Newest first: id, date, sender, kind, title, status |
| `GET /{id}` | The whole item; 404 when unknown |
| `PUT /{id}/status` `{status}` | The item with the new status; 400 for a status that is not one of the four; 404 when unknown |

## 7. Facts

Both are operational, shown in the audit log, with the item as the subject.

| Fact | When | Data |
|---|---|---|
| `modbot.feedback.send` | An item is saved | The kind only |
| `modbot.feedback.status` | The status is changed to a different one | The status only |

Neither carries the title or the details, so the log holds no copy of the words. The write and its
fact are in one transaction. Saving is one class (`FeedbackWriter`) so the chat tool of step 5 saves
in the same way and leaves the same fact.

## 8. Later steps

2. The Feedback page (list with date, sender, kind, title and status; open one; set the status;
   empty state; phone width), a menu entry for those who hold Review feedback, the link preview the
   repository rules ask for, and the docs page.
3. The button on the companion's main window and on the desktop overlay strip, sharing Dictate's
   microphone so only one listens at a time, and the draft window with kind chips, title, details,
   Cancel and Send. The main window has no Dictate button, so its Feedback button sits at the top of
   the sidebar under the group's name, where it is easy to find (user, 2026-11-18). The desktop
   overlay's sits next to Dictate. Both are hidden until the server's Send feedback permission offers
   them. The speech model and its download are Dictate's, shared.
4. The same on the headset panel; its draft panel can be locked, made click-through, grabbed and
   moved, and its spot is saved, like every other VR panel. Built as a third row on the show and hide
   button (a column would squeeze the labels in its square card) and a draft panel of its own, with
   its own saved spot (`overlayFeedbackSpot`), apart from the chat panel's. The text in it is read-only
   (the headset has no keyboard); only the kind is chosen. A draft opens in the headset only when the
   headset button started it. Long title and details are cut in the view only, at 2 and 9 lines.
5. `file_feedback` (kind, title, details): an acting chat tool, so it is off until an admin switches
   it on in Settings, AI, Chat. It saves as the person asking. When it is offered, the chat's
   instructions tell the model to offer to file a bug, a missing feature or a wish for a change, to
   show the draft with names removed, and to call the tool only after the person says yes.
   Built as `FileFeedbackTool` (`src/Modbot.Api/Features/Chat/Tools/FeedbackTools.cs`), which needs
   Send feedback (the registry leaves it out for anyone without it, and it checks again when run) and
   saves through `FeedbackWriter`, naming the asking person as sender. The registry lists it in
   Settings, AI, Chat as the one tool that acts, so no web change was needed. The system prompt
   (`ChatPrompt.Build`'s `canFileFeedback`) carries the feedback rules, and says "you can only look
   things up" or not, only when the tool is offered; when it is not offered the prompt does not
   mention feedback at all. The same tool is offered over MCP when it is on, as any chat tool is.
