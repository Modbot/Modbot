# Modbot — The cloud backup stays out of the way

- **Date:** 2026-09-19
- **Status:** Implemented with this document
- **Covers:** what the companion's screens say about where an event went; the one word an Events row
  shows; the removal of every mention of the backup from the client's window
- **Related:** the cloud event backup design (2026-09-15), which this narrows; `Journal/SentJournal.cs`,
  `Presentation/EventFilters.cs`, `Presentation/EventRowDetail.cs`, `MainWindow.cs`,
  `MainWindow.Events.cs`, `PRIVACY_POLICY.md`

---

## 1. What the maintainer asked for

> "we need to kill the references to modbot cloud instead of showing which server it was sent to
> including the filter and stuff it needs to show just "sent" and it should show as sent if at least
> one of the two servers accepted it, even if modbot cloud is responding later, and if modbot cloud
> fails, as long as the main sent it should show as "sent" instead of failed. Should not show errors
> of Modbot Cloud displaying it. Modbot Cloud is entirely a hidden cloud backup solution that can be
> disabled in env variables should the user want to, but by default the policy is don't announce,
> don't show, don't indicate that modbot cloud exists. Users can find this in privacy policy if they
> are curious about where data is going"

The screen that prompted it showed two pills on every row: the moderator's own group with **sent**,
and "Modbot Cloud" with **sent** or **waiting** under it. A row where the group's server had the
event and the backup had not yet answered read as half-finished. A row where the backup had given up
on a batch read as **failed** — and "failed" on that page means a group's record is missing
something, which was not true.

## 2. The rule

A row says one word. That word is `JournalRow.State`:

| The paired server | The backup | The row says |
|---|---|---|
| sent | anything, including failed or nothing | **sent** |
| waiting | sent | **sent** |
| waiting | waiting, failed, or nothing | **waiting** |
| withheld | anything | **withheld** |
| failed | anything | **failed** |
| nothing — no paired server was ever given it | anything | **nothing at all** |

In one sentence: **the paired server decides the word whenever it has settled on one, and the backup
can only ever lift a waiting row to sent.** The backup can never make a row worse.

### 2.1 Why "sent" when only the backup has it

The maintainer's rule is "sent if at least one of the two accepted it", and a waiting row where the
backup has the event is exactly that case: a copy of what the client observed exists somewhere other
than this PC, so the honest word is sent rather than waiting. In practice it is rare — a paired
server is sent a batch within seconds of a change, while a backup batch closes at 500 events, 256 KB
or 60 seconds — so it is the tail of a server that is briefly unreachable, and the Servers page still
says in plain words how many observations are queued for that server and that none are lost.

### 2.2 Why "withheld" and "failed" still win

Two cases where "at least one accepted it" would produce a sentence the moderator would be right to
be angry about:

- **Paused.** Pausing a paired server is the moderator's own decision, and the client's own words for
  it are "Nothing about what you do is being captured or sent to this server." The backup is a
  separate flow and keeps going. If a paused row read **sent**, the screen would be contradicting the
  choice the person just made. It reads **withheld**.
- **Refused for good.** A server that refuses a batch as malformed or too large has dropped those
  events; that group's record really is missing them, and there is a warning about it. It reads
  **failed**.

Both are statements about the moderator's own group's record, which is what the page is for. The
backup having a copy does not repair either of them, so it does not change either word.

### 2.3 Why an event no paired server was given says nothing

Every instance outside a managed group — public, friends-only, private, and everything at all for a
client with nothing paired — produces a journal row, because the backup wrote one. Those rows stay on
the page: it lists every event the client handled, and quietly dropping the ones that went only to
the backup would be a different and worse kind of hiding. They carry the time and the sentence and no
state word, which is what a row about no group's record can honestly say.

## 3. Everywhere a mention was taken out

| Where | What it was | Now |
|---|---|---|
| `MainWindow.cs`, `EventRow` | A second pill per row, labelled "Modbot Cloud", with its own sent / waiting / failed word | One pill, from `JournalRow.State`, labelled with the group |
| `EventFilters.cs` | A `Destination` filter property whose two values were `server` and `cloud`, the second labelled "Modbot Cloud" | The property is gone, with `ServerValue`, `CloudValue`, `DestinationsOf` and `LabelOf` |
| `EventFilters.cs`, `StatesOf` | Up to two words per row, one per place | One word per row |
| `EventRowDetail.cs` | Two fields, "Server state" and "Modbot Cloud" | One field, "State" |
| `EventRowDetail.cs`, `ToJson` | The whole `JournalRow`, `cloudState` included | The row as the page has it: time, summary, server, state, seen, note |
| `MainWindow.Events.cs` | Chip values drawn through `EventFilters.LabelOf`, which existed only to write "Modbot Cloud" | The values as they are |
| `docs/companion/install.mdx` | The Events page described as showing "where it went (the paired server, Modbot Cloud, both or neither)"; **Destination** listed among the filter chips; "What went to Modbot Cloud is listed on **Events**" | The page described as showing one state; no Destination chip; a plain statement that the window does not show the backup |

A chip on `destination` left in somebody's `settings.json` by an older version is dropped when the
filters are read (`EventFilterSet.Parse`), so the page opens with one filter fewer rather than
falling over on a chip it can no longer draw.

Nothing else in the window mentioned it. `CompanionAppState`'s warnings never did: a backup that
cannot be reached produces no warning, because it never produced one — it backs off and retries, and
`CloudBackupStatus` is read by nothing that draws.

## 4. What deliberately stays

- **The backup itself, unchanged.** Same flow, same outbox, same retries, same settings, same
  `MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED`. This is a change to what is displayed.
- **`SentJournal.CloudName`, and the lines the backup writes.** Every line it wrote is still appended
  to `%APPDATA%\Modbot\sent.jsonl`, saying what was sent and where. `JournalRow.CloudState` is still
  folded out of those lines; it is simply not shown. A guard test pins the name to the two files that
  may use it — the backup and the journal — and forbids it, `CloudState` and `JournalDestination.Cloud`
  anywhere in the window.
- **The client's log file.** It still says whether the backup is on, where it points, and when it hit
  a problem. "What did the backup do" stays answerable to anybody who looks.
- **Every comment and class remark that says what leaves the machine.** The guard test that makes
  files touching disk or network disclose themselves in plain language is untouched, and so is the
  prose it guards. Hiding something from a screen is not a licence to make the source vague about it.
- **The privacy policy**, which now also states the two things a person can no longer learn from the
  window: that it does not mention the backup, and that pausing a paired server does not stop it.

## 5. Where the promise had to be kept honest

The strongest claim `SentJournal` makes is that the Events page is "the answer for everybody else: a
screen, on demand, showing exactly what left the machine and where it went." That claim is narrowed
by this change, and pretending otherwise would be the dishonest version of it. So:

1. **The record did not move; only the screen did.** `sent.jsonl` is still complete and still
   append-only, and the class remark still says so.
2. **The policy says what the screen no longer does.** `PRIVACY_POLICY.md` now states plainly that
   the client's window does not mention the backup, that pausing does not stop it, and where to look
   for what it did. `docs/companion/settings.mdx` says the same beside the switch that turns it off.
3. **Nothing was reworded to become untrue.** No comment, remark or guard was softened. The one piece
   of prose that became false was the guard test's own note saying the Events page "is allowed to say
   that Modbot Cloud is one of the places an event went"; it has been replaced by the narrowing and
   the reason for it.

The JSON box under an opened row is the one place where this is visible as a small loss: it used to
be the row exactly as the window received it, and it is now the row as the page has it. The remark
says so, and points at the file for the whole record.

## 6. Decided against

- **Not journalling the backup's lines at all.** It would have made the screen and the file agree
  again, at the price of deleting the client's own record of what left the machine. The file is the
  thing that makes the privacy policy checkable; it stays.
- **Hiding rows that only the backup was given.** Those are the private and public instances. Leaving
  them on the page with no destination is odd-looking; removing them would mean the page no longer
  lists every event the client handled, which is a bigger lie than an odd-looking row.
- **"sent" for a paused or refused event because the backup took it.** §2.2.
- **A "somewhere else has it" state, or a second, quieter word.** Any word at all is an indication,
  and the instruction was that there be none.
- **Removing `JournalDestination.Cloud` from the journal format.** Old files would stop reading
  correctly, and the destination is how the two halves' lines are told apart when a row is folded.
