# Modbot — Google Calendar

- **Date:** 2026-10-03
- **Status:** Step 1 built (§4; §7 says what it built and where it differs): the key, Check and the Links.
  Steps 2 and 3 (sending events, the form, the preview, the feed button) are not built. The owner
  took every recommended decision in §5 on 2026-10-03.
- **Covers:** Modbot writing its events into a Google calendar the group's owner owns, through a
  service account; the settings and Check; what goes to Google and when; at-most-once inserts; limits;
  the public links ("Add to Google Calendar")
- **Depends on:** calendar design (2026-09-15: places, fingerprints, `CalendarFeedWriter`, the feed
  and the public feed), foundation §4.4 (`IModbotClock`), §8.3 (`ISecretProtector`)
- **Started from:** the 2026-10-03 research report. Facts in §1 were read from Google's own pages on
  2026-10-02/03; **(unconfirmed)** marks anything that was not

---

## 0. Summary

**Build it. A is the real feature, and B mostly becomes showing that calendar's links.**

- **A: Modbot writes into a calendar the server owner owns.** The owner shares the calendar with a service account using "Make changes to events". Modbot signs in with the account's JSON key. This needs no OAuth app, no consent screen and no Workspace domain-wide delegation, so the 7-day refresh-token problem never comes up. Service-account keys never expire by default.
- **It fits `CalendarEventPlace`.** Google events can be edited, like VRChat's and Discord's. Add one new place, `googleCalendar`: one row per event, fingerprint-driven, with states Waiting, Published, Failed and Removed.
  - A repeating event is one Google event with `RRULE`/`EXDATE` lines. `CalendarFeedWriter.Rule` already builds them.
  - A moved or reworded date is a change to that one date on Google.
- **At most once with an id Modbot picks.** Modbot writes the Google event id (`mb` + base32hex of the event's Guid + a turn number) on the row before the first insert. Google answers a repeated id with **409 duplicate**.
  - Google also says "we cannot guarantee that ID collisions will be detected at event creation time".
  - So after any insert whose answer was lost, Modbot first reads the id back (`events.get`) and inserts only on a 404.
  - This needs nothing like VRChat's adopt-on-500 search through the month.
- **Plain HTTP, no package.** There are seven small calls. RS256 is `RSA.ImportFromPem` plus `SignData(…, SHA256, Pkcs1)`, and `Base64Url` is already used by the feed endpoint. `Google.Apis.Calendar.v3` would pull in `Google.Apis`, `Google.Apis.Auth` and Newtonsoft.Json for that.
- **Cost:** free. The default quota is 600 requests a minute per user and 1,000,000 a day per project, and Modbot would use well under 1% of it. Google plans to charge for going **over** the quota "later in 2026".
- **B:** once A exists, the public Google calendar's own subscribe link, public page and iCal address are the safe things to hand out. B should be:
  1. show those links in Settings;
  2. add an "Add to Google Calendar" button beside the secret feed's Copy, for staff's own calendars.

  Per-event template links are not worth building (§3.9).
- **Size: M**, about 2 to 2½ weeks, in three steps that can each ship (§4).

## 1. Facts

| # | Fact | Source | Status |
|---|---|---|---|
| 1 | Event `id`: "lowercase letters a-v and digits 0-9" (base32hex, RFC 2938), "between 5 and 1024 characters", "must be unique per calendar". "We cannot guarantee that ID collisions will be detected at event creation time." UUID-based ids are recommended. | developers.google.com/workspace/calendar/api/v3/reference/events/insert | confirmed |
| 2 | 409, reason `duplicate`, "The requested identifier already exists." Action: "generate a new ID … or use the update method". | …/calendar/api/guides/errors | confirmed |
| 3 | Inserting with the id of a **deleted** event also gives 409, because deleted events stay as `status: cancelled`. Bringing one back is an update with `status: confirmed`. | community reports (Nylas guide, GitHub issues) | **(unconfirmed)** |
| 4 | `recurrence`: "List of RRULE, EXRULE, RDATE and EXDATE lines … as specified in RFC5545". No DTSTART/DTEND lines. `start.timeZone`/`end.timeZone` are required for recurring events and set the zone the repeat is expanded in. | events resource, recurring events guide | confirmed |
| 5 | `EXDATE;TZID=Zone:yyyyMMddTHHmmss` lines and `COUNT` counting excluded dates follow RFC 5545 in Google. | RFC 5545 plus fact 4 | **(unconfirmed)** in Google itself |
| 6 | One date of a series: instances carry `recurringEventId` and `originalStartTime`. They are found with `events.instances` (parameter `originalStart`) and updated by PUT on the instance id. A date is cancelled by setting the instance's `status` to `cancelled`. "Do not modify instances individually when you want to modify the entire recurring event." | recurring events guide; events.instances | confirmed |
| 7 | Instance ids look like `{eventId}_{yyyyMMddTHHmmssZ}`. | common use | **(unconfirmed)**; design doesn't depend on it |
| 8 | Changing the series (time or rule) may drop or detach dates changed on their own. | community | **(unconfirmed)**; design re-sends them anyway |
| 9 | `update` "does not support patch semantics and always updates the entire event resource". | events.update | confirmed |
| 10 | `visibility`: `default`, `public` ("visible to all readers"), `private` ("only event attendees may view event details"), `confidential`. | events resource | confirmed |
| 11 | A `private` event on a public calendar shows to the public as a busy block with no title. | inference from fact 10 | **(unconfirmed)** |
| 12 | `reminders`: "Information about the event's reminders **for the authenticated user**". Up to 5 overrides. | events resource | confirmed. Reminders set by Modbot would only be the service account's own. |
| 13 | `colorId` points into the `event` palette of `colors.get`. | events resource | confirmed. Whether subscribers see event colours: **(unconfirmed)** |
| 14 | `description` "Can contain HTML". `location` is free text. `source` = {`url` (http/https), `title`}. `htmlLink` is read-only. `extendedProperties.private` is private to this calendar's copy. | events resource | confirmed |
| 15 | `attachments`: Drive files only (`alternateLink` format), need `supportsAttachments=true`, at most 25. | events resource | confirmed. **The stored cover picture can't be sent.** |
| 16 | Scopes for insert, update, delete: `calendar`, `calendar.events`, `calendar.app.created`, `calendar.events.owned`. `events.list` also takes `calendar.events`. `acl.list` takes `calendar`, `calendar.acls`, `calendar.acls.readonly`. | reference pages | confirmed |
| 17 | `events.list` returns the calendar's `summary`, `timeZone` and the caller's `accessRole` (`none`, `freeBusyReader`, `reader`, `writerWithoutPrivateAccess`, `writer`, `owner`). | events.list | confirmed. **Check can read all three with no extra scope.** |
| 18 | `writer` "can also see ACLs". An ACL scope can be a user, group, domain, or "the public" (type `default`). | sharing concepts | confirmed |
| 19 | Sharing a personal calendar with a service account's email and "Make changes to events" lets the account write to it **without domain-wide delegation**. | community guides (Medium, HaloCRM, Google Groups) | **(unconfirmed)** by a Google page; widely reported |
| 20 | "Make changes to events" = the `writer` role. | common use | **(unconfirmed)** |
| 21 | Service accounts can't add **attendees** without domain-wide delegation (403 `forbiddenForServiceAccounts`). | GitHub discussions, Google dev forum | **(unconfirmed)**; Modbot adds none |
| 22 | Service-account JWT: header `alg` RS256 (`kid` optional). Claims `iss` (account email), `scope` (space-separated), `aud` "always `https://oauth2.googleapis.com/token`", `iat`, `exp` at most 1 hour after `iat`. POST with `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`. Clock-skew error: "Token must be a short-lived token (60 minutes) and in a reasonable timeframe". | developers.google.com/identity/protocols/oauth2/service-account | confirmed |
| 23 | Key file fields: `type`, `project_id`, `private_key_id`, `private_key` (`-----BEGIN PRIVATE KEY-----`, PKCS#8), `client_email`, `client_id`, `auth_uri`, `token_uri`, cert URLs. Keys "never expire" by default. At most 10 keys per account. | docs.cloud.google.com/iam/docs/keys-create-delete | confirmed |
| 24 | `iam.disableServiceAccountKeyCreation` is enforced by default for **organisations** created on or after 2024-05-03 (Workspace or Cloud Identity). A personal Gmail project has no organisation, so key creation works. | same page; secure-by-default docs | first half confirmed; personal-account half **(unconfirmed)** |
| 25 | Google disables service-account keys it finds leaked publicly. | Google Cloud blog title only | **(unconfirmed)** |
| 26 | OAuth apps in "Testing" with external users get refresh tokens "expiring in 7 days" unless only profile scopes are asked for. | developers.google.com/identity/protocols/oauth2 | confirmed (why OAuth is not used) |
| 27 | Quota: 10,000 requests/min per project, 600/min per user per project. Over quota: "403 usageLimits" or "429 usageLimits". Exponential backoff is recommended. "All standard use … is available at no additional cost." 1,000,000 requests/day per project with no charge. "Exceeding the quota request limits is planned to incur charges … later in 2026." Page updated 2026-09-11. | …/calendar/api/guides/quota | confirmed |
| 28 | Other 403 reasons: `rateLimitExceeded`, `userRateLimitExceeded`, `quotaExceeded` ("Calendar usage limits exceeded."). 429 `rateLimitExceeded`. 410 `deleted`. 412 `conditionNotMet`. | errors guide | confirmed |
| 29 | Calendar use limits: more than 100,000 events in a short period cuts creation, possibly for "several months". 750 shares, 60 new calendars (hours). Written for Workspace admins; whether they apply to consumer accounts isn't said. | knowledge.workspace.google.com/admin/calendar/avoid-calendar-use-limits | confirmed text; consumer scope **(unconfirmed)** |
| 30 | Enabling the Calendar API needs no billing account. | follows from fact 27's "no additional cost" | **(unconfirmed)** |
| 31 | Public calendar: Settings → "Make available to public" (full details, or "See only free/busy"). Links: "Get shareable link", "Public URL to this calendar", "Public address in iCal format" ("only works if the calendar is public"). A searchable site embedding it can show up in search. | support.google.com/calendar/answer/37083 | confirmed |
| 32 | Subscribe by URL: computer only ("can't subscribe … in the Google Calendar app for Android, iPhone, or iPad"). "From URL" adds a public/published calendar. | support.google.com/calendar/answer/37100 | confirmed |
| 33 | How often Google re-reads a URL-subscribed iCal feed: commonly 12 to 24 hours or more, with no way to force it. | community, other vendors | **(unconfirmed)**; Google gives no number |
| 34 | Template link `calendar.google.com/calendar/render?action=TEMPLATE` with `text`, `dates` (`…Z/…Z`), `ctz`, `details` (simple HTML), `location`, `src`. `recur` "is **not** handled by the client side deep link parser". Google has no official page for it any more. | InteractionDesignFoundation add-event-to-calendar-docs | **(unconfirmed)** (not Google) |
| 35 | `https://calendar.google.com/calendar/r?cid=<webcal or https URL, or calendar id>` opens "add this calendar". | common use | **(unconfirmed)** |
| 36 | `Google.Apis.Calendar.v3` 1.75.0.4206 (2026-07-16), targets net6.0/netstandard2.0/net462, needs `Google.Apis` and `Google.Apis.Auth`. Those bring Newtonsoft.Json. | nuget.org | first part confirmed; Newtonsoft **(unconfirmed)** from memory |

## 2. Where Google fits in Modbot's code

- **Place rows fit.** `CalendarEventPlace` (`src/Modbot.Core/Data/Entities/CalendarEvent.cs`) is one row per event and place, with `ExternalId`, `SentFingerprint`, `FailedFingerprint`, `Error`/`ErrorAt` and states Waiting/Published/Failed/Removed. Google matches VRChat's shape: one series object per event that is edited in place, plus per-date changes. Unlike Bluesky (never edited, several posts per event), it needs no table of its own.
- **The repeat is already written as iCalendar.** `CalendarFeedWriter.Rule(event, zone)` gives `FREQ`, `BYDAY`, `INTERVAL` (+`WKST=MO`), `UNTIL` (UTC, last moment of the last day) or `COUNT`. The `EXDATE` and time formatting (`Time`, `IsUtc`) are private; expose one helper, `CalendarFeedWriter.ExDate(change, zone)` or a small `ICalText` class, so the feed and Google can't drift apart. `Entry(...)` gives title, notes and location.
- **One-date changes** (`calendar_date_change`) hold VRChat's per-date state (`VRChatSentFingerprint`, `VRChatFailedFingerprint`, `VRChatError`, `VRChatErrorAt`, `VRChatId`, `VRChatSentStartsAt`).
  - `CalendarDates.MayBeOnVRChat` and `CanForget` decide when a date put back as planned may lose its row.
  - `CalendarVRChatPublisher` removes the row once VRChat has the plain date (line ~623).
  - **This is the one shared-code trap.** If VRChat drops the row first, Google keeps the moved copy for good. Both rules must learn about Google (§3.5).
- **Readiness.** `CalendarReadiness` (`src/Modbot.Api/Features/Calendar/CalendarReadiness.cs`) works from settings only. Add `Google`. `CalendarReadyView(bool VRChat, bool Discord)` gets a third field. The web side is `src/Modbot.Web/src/lib/calendarPlaces.ts` (`DESTINATIONS`, `DESTINATION_SWITCH`, `SET_UP_LINK`, `isSetUp`).
- **Secrets.**
  - `ISecretProtector` (AES-GCM, key in the database) protects `Settings.*Encrypted`.
  - `AiSettingsEndpoints` is the model: the key is never returned, there is `ApiKeyStored`, and a Test works from the form's values.
  - `IntegrationsHandler` audits secrets with `.Secret(name, changed)`.
  - `LogSecrets.SecretNames` already catches `privatekey`, `private_key`, `token`, `secret` and `signature`. Add `assertion` (the token request's form field) and `jwt`.
- **Guarded HTTP is not needed.** `PictureLinks.GuardedHandler()`/`PublicAddresses` protect fetches from addresses a person chose. Google's hosts are fixed: `oauth2.googleapis.com` and `www.googleapis.com`.
  - **The key file's `token_uri` is ignored**, so a crafted file can't make Modbot post a signed token anywhere else.
  - The calendar id goes into the path, so it is escaped with `Uri.EscapeDataString`, which stops `../` tricks.
  - Use a named `HttpClient` with no redirects and no proxy.
- **Cover picture.** `CalendarCoverPicture` is served only behind See calendar (`/api/calendar/covers/{id}`), and Google event attachments take Drive files only (fact 15). **No picture goes to Google.**
- **Featured** is VRChat's. Google has no equivalent; a colour is the only stand-in (decision 8).
- **Who sees it.** `CalendarEvent.Visibility` (`group`/`public`) is shown only in the VRChat section of `CalendarEventForm.tsx`. `AccessType` (Who can join) is in "Where".
- **Loops.** VRChat's `CalendarService` (15 s) moves states and writes VRChat. `CalendarDiscordService` (20 s) runs only while the bot is connected. Google gets its own Api-side hosted service, like `WebhookDeliveryService` in `ApiSurface.cs`, so it doesn't depend on either.
- **Facts.** `modbot.calendar.publish.done/fail/remove` carry `place`; adding `googleCalendar` is enough. `factSentence.tsx` needs the label.
- **Integrations and Settings.** Add `google` to `IntegrationId`/`SettingsTopic` in `src/Modbot.Web/src/lib/integrations.ts`, add an icon in `Integrations.tsx`, and add a `TABS` entry in `src/Modbot.Web/src/pages/Settings.tsx`, group "Connections".
- **The feed.** `GET /api/calendar/feed/{token}.ics` is anonymous. Its token is 32 random bytes, stored hashed and encrypted. It is shown and replaced in `FeedRow` (`src/Modbot.Web/src/pages/Calendar.tsx`) for Manage calendar. It lists every Scheduled/Open event, plus finished and cancelled ones for 30 days.

## 3. Design

### 3.1 Settings topic "Google Calendar" and the Check

**Controls** (group Connections, Manage settings):

| Control | Notes |
|---|---|
| **Key file** | A file picker for the `.json`. The browser reads it and sends the text (refused over 16 KB). Shows **Saved** once kept; never shown back. Refused unless `type` is `service_account` and `client_email`, `private_key` and `private_key_id` are there: "This is not a Google key file." |
| **Modbot's Google address** | The key's `client_email`, read-only, with **Copy**. It's what the owner shares the calendar with. |
| **Calendar ID** | Text. Also takes a pasted public URL, embed link or iCal address, and keeps the `src=`/path id from it. |
| **Check** | See below. |
| **Sending** | On/off. Off until the first good Check. The one place to stop all Google writes. |
| **Existing events** | **Add all**. Ticks Google Calendar on every Scheduled/Open event that may go (members-only rule), audited. Shown after a good Check (decision 6). |
| **Remove Modbot's events** | Deletes every event Modbot put on the calendar, one per call, through the loop; asks first (decision 7). |
| **Forget** | Removes the key, calendar id and Check results. Events on Google stay unless removed first. |
| **Links** | Shown when Check says Public: **Subscribe**, **Public page**, **iCal address**, each with **Copy** (§3.9). |

**What Check does.** It writes nothing.
1. Parses the key and signs a JWT with `IModbotClock` times. Scope: `calendar.events calendar.acls.readonly`.
2. POSTs to `https://oauth2.googleapis.com/token` (the key's `token_uri` is ignored).
3. Calls `GET /calendar/v3/calendars/{id}/events?maxResults=1&fields=summary,timeZone,accessRole`. That gives the calendar's name, its time zone and Modbot's role (fact 17).
4. Calls `GET /calendar/v3/calendars/{id}/acl?fields=items(scope,role)`, looking for a `default` scope: `reader` means public with details, `freeBusyReader` means free/busy only. A 403 shows **Unknown**.
5. Shows: **Calendar**: name · **Time zone** · **Can change events: Yes/No** (`writer`/`owner` = Yes) · **Public: Yes / Free or busy only / No / Unknown**.

Errors, as plain sentences:
- "Google did not accept the key." (`invalid_grant`, deleted key)
- "The server's clock is off." (the skew error, fact 22)
- "Modbot can't see this calendar." (404)
- "Modbot can only read this calendar." (`reader`)
- "Google is limiting Modbot. Try again after 21:40."

**Kept on `Settings`:**
- `GoogleClientEmail`, `GoogleKeyId`, `GoogleProjectId` (shown, not secret)
- `GooglePrivateKeyEncrypted`: only the PEM; the rest of the file is dropped
- `GoogleCalendarId`
- `GoogleSendingOn`
- `GoogleCheckedAt`, `GoogleCalendarName`, `GoogleCalendarTimeZone`, `GoogleCanChange` (bool), `GooglePublic` (`all`/`freeBusy`/`no`/null), `GoogleProblem`
- `GoogleStoppedUntil` (cold stop)

**Signing.** A `GoogleSignIn` singleton keeps the access token in memory until 5 minutes before `expires_in`, with at most one token request a minute. Nothing about the token is stored. The RS256 code, for the builder:

```csharp
using var rsa = RSA.Create();
rsa.ImportFromPem(privateKeyPem);                                   // PKCS#8 "BEGIN PRIVATE KEY"
var head = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = keyId }));
var body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new {
    iss = clientEmail, scope = Scopes, aud = "https://oauth2.googleapis.com/token",
    iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(55).ToUnixTimeSeconds() }));
var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{head}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var assertion = $"{head}.{body}.{Base64Url.EncodeToString(sig)}";
```

`now` comes from `IModbotClock`. The decrypted PEM lives only for the call.

**Audit.** `googleKey` as a secret (changed or not); `googleCalendarId` and `googleSending` as fields.

### 3.2 Data model

- `CalendarPlaces.Google = "googleCalendar"`.
- `CalendarEvent.PublishToGoogle` (bool).
- `CalendarEventPlace`: reuse `ExternalId` (the Google event id) and add `GoogleCalendarId` (the calendar it was written to). If Settings later names another calendar, the old copy is deleted there and the event is made on the new one.
- `CalendarDateChange`: `GoogleSentFingerprint`, `GoogleFailedFingerprint`, `GoogleError`, `GoogleErrorAt`.
- One migration. No `NULLS NOT DISTINCT`.

**Event id:** `mb` + base32hex(Guid bytes) (26 characters) + the turn number (`0`, `1`, …), written on the row before the first insert. A place taken down and wanted again (`Revive`) gets the next turn. A deleted Google event keeps its id as a cancelled event (fact 3), so a new id is safer than bringing the old one back.

### 3.3 What goes to Google, and when

One pure function, `CalendarGoogle.Wants(event, settings)`, is used by the loop, the form, readiness and the tests. An event goes only when all of these hold:
- Sending is on, Check passed, and `GoogleCanChange` is true;
- the event is ticked, not a draft, not deleted;
- Scheduled, Open or Finished (finished stays as history, like VRChat);
- or Cancelled under decision 3 B;
- not members-only under decision 1.

**The body** (`CalendarGoogleBody.For(event, worldNames, settings)`, shared by the loop and the preview):

| Field | Value |
|---|---|
| `id` | §3.2 |
| `summary` | Title (`Cancelled: ` in front under decision 3 B) |
| `description` | The event's description, HTML-escaped (`&lt;`, `&gt;`, `&amp;`), line breaks kept. Then a blank line and `World: {name}` and the world page `https://vrchat.com/home/world/{id}` (decision 4). |
| `location` | The world's name, or `VRChat` with none (same as `CalendarFeedWriter.Entry`) |
| `start`/`end` | `dateTime` with offset, plus `timeZone` = the event's IANA zone |
| `recurrence` | `RRULE:` + `CalendarFeedWriter.Rule(...)`, plus one `EXDATE;TZID=…:…` per cancelled date (the feed's §6 choice: a cancelled date is part of the repeat itself) |
| `visibility` | `default`, or `private` under decision 1 B |
| `transparency` | `transparent`, so the owner's own free/busy isn't blocked by group events |
| `reminders` | `{useDefault: true}`. Reminders are per user (fact 12), so Modbot sets none. |
| `source` | `{title: group name, url: world page}` when there is a world |
| `extendedProperties.private` | `{modbotEvent: "<guid>"}`, so a read-back can prove the event is Modbot's own |
| `colorId` | none (decision 8) |
| query | `sendUpdates=none`. There are no attendees ever. |

**The fingerprint** covers: calendar id, id, summary, description, location, start, end, zone, recurrence lines, visibility. It leaves out the event's Open/Scheduled state and the instance, so a date opening costs no write.

**Pace.** The loop runs every 20 s with up to 10 calls a pass (at most 30 a minute against Google's 600). The 20 s settle is the same as VRChat's, so quick edits fold into one write.

### 3.4 At most once

```
due place, ExternalId null
 └─ ExternalId = mb…{turn}, ErrorAt = now (sent at), SentFingerprint = null → SAVE   (written before sending)
     └─ POST events (id fixed)
          200         → Published, SentFingerprint = fp
          409         → GET id: modbotEvent = ours, status confirmed → adopt; then PUT if fp differs
                                 ours but cancelled → Revive with next turn (new id)
                                 not ours → Failed "The calendar already has another event with this id."
          timeout/5xx → stays Waiting (shown "Sending…"); next pass, at least 1 min on:
                          GET id: 200 ours → adopt;  404 → POST again, same id;  GET failed → wait
          401         → new token once, then the same
          403 limit / 429 → lane stopped (§3.7), nothing sent before then
          other 4xx   → Failed with Google's words, held until the event changes (FailedFingerprint)
```

- **The rule:** a row with an id and no `SentFingerprint` is always read back before any insert. Fact 1 says Google doesn't promise to catch a duplicate id, so the 409 alone isn't trusted.
- **Edits:** `PUT events/{id}` with the full body (fact 9). 404 or 410 on a PUT means it was deleted on Google; the place goes back to insert with the next turn.
- **Removal** (chip off, Delete, Sending's **Remove Modbot's events**, cancel under decision 3 A, calendar id changed): `DELETE events/{id}`. 404 or 410 counts as done → Removed, fact `publish.remove`.
- **Try again** uses the existing route `POST /api/calendar/events/{id}/googleCalendar/try-again` with `plannedStartsAt` for one date. It clears the failure. An insert whose outcome is unknown is still read back first.
- **Changes made on Google aren't read back.** Modbot's next write replaces them. The docs say: edit in Modbot.

### 3.5 One-date changes

- **Cancelled date:** an `EXDATE` in the series, so it changes the series fingerprint and needs no per-date state. If that date already had its own copy sent to Google, its instance is also set `status: cancelled` (fact 6), because whether an `EXDATE` hides a changed copy is unconfirmed.
- **Moved or reworded date:**
  1. `GET events/{id}/instances?originalStart={planned, RFC3339}&maxResults=1`.
  2. PUT that instance with its start, end, summary and description.
  3. Write `GoogleSentFingerprint`.
  - Not found → the date fails with "Could not find this date on Google." and is held until it changes.
- **After any series write,** every changed date still to come has its `GoogleSentFingerprint` cleared and is sent again (fact 8). This is the same rule VRChat follows (calendar spec §2.2).
- **A date put back as planned:** the instance is PUT with the planned time and the event's words, then `GoogleSentFingerprint` is cleared.
- **Shared rule change:**
  - Add `CalendarDates.MayBeOnGoogle(change)` (= `GoogleSentFingerprint` not null).
  - `CanForget` = plain, not on VRChat, not on Google.
  - `Rematch` clears the Google fields when the planned start moves.
  - `CalendarVRChatPublisher` removes a plain row only when `CanForget` holds; today it removes it as soon as VRChat has the plain date.
  - The Google publisher removes it on the same test.

### 3.6 Cancel, delete, off and on

| What happens in Modbot | On Google |
|---|---|
| Edit | PUT after 20 s settle |
| Cancel event (decision 3 A) | Deleted |
| Cancel event (decision 3 B) | One-off: `Cancelled: Title`, kept until a day after it would have ended, then deleted. Repeating: the series is cut at the next date (`UNTIL` on that day), that date shows `Cancelled: …`, and the series is deleted a day after it. |
| Delete | Deleted at once |
| Finished | Kept as history |
| Chip off | Deleted |
| Chip on again | New id (next turn), inserted |
| Settings → Sending off | Nothing changes on Google; nothing more is sent (decision 7) |
| Settings → Remove Modbot's events | Every Google place is deleted through the loop |
| Calendar ID changed | Deleted from the old calendar (failures there are only noted), inserted into the new one |
| Key forgotten | Places stay as they are; the chip says **Not set up** |

### 3.7 Limits (never retry a 429)

- **429**, or **403** with reason `rateLimitExceeded`, `userRateLimitExceeded` or `usageLimits`:
  - set `GoogleStoppedUntil` to now plus 15 min, or `Retry-After` if Google sends one and it is later;
  - places stay Waiting (shown **Sending…**);
  - no call of any kind before then, and one probe after.
- **403 `quotaExceeded`** ("Calendar usage limits exceeded"): stopped for 6 hours. A Health line says **Google Calendar: limited until 03:10**.
- **Other 403s** (`forbidden`, `insufficientPermissions`, `requiredAccessLevel`): Failed with "Modbot can't change events on this calendar." Held until the next good Check.
- A token request refused for the key stops the lane and sets `GoogleProblem`. Nothing more is tried until a new key or a Check.

### 3.8 The form, the event, Health and Integrations

- **Chip "Google Calendar"** in "Where it goes", after Calendar feed.
  - **Not set up** links to `/settings#google`.
  - **Members only**, a single status word, when decision 1 A blocks it.
- **"Visible to" moves** from the VRChat section to "Where", shown when VRChat or Google Calendar is on (the same move Bluesky's design asks for).
- **Preview tab:** a Google card drawn from `CalendarGoogleBody`: title, date and time in the event's zone with the repeat in words, location, description with the world line, the calendar's name, and a padlock for `private`.
- **The event's box:** **Google Calendar: Sending… / Published / Failed (Google's words) · Try again / Removed**. When published, **Open** links to the event's `htmlLink`. Per-date failures appear as for VRChat.
- **Health:** Calendar card lines for Google failures and **Not set up**, plus the limit line above.
- **Integrations:** a "Google Calendar" card. Parts **Key · Calendar · Sending**. Status **Needs setup / Working / Failed / Off**.
- **UI words:** Google Calendar, Key file, Modbot's Google address, Calendar ID, Check, Can change events, Public, Free or busy only, Sending, Add all, Remove Modbot's events, Forget, Subscribe, Public page, iCal address, Copy, Members only, Not set up, Sending…, Published, Failed, Try again, Open.
  - **Never shown:** service account, JWT, scope, ACL, access role, RRULE, EXDATE, base32hex, instance.
  - No explanatory text (CLAUDE.md). The how-to belongs in the docs.
  - No new page, so no new link-preview tags.
  - Every time comes from `IModbotClock`, including the JWT's `iat`/`exp`.

### 3.9 B: what "Add to Google Calendar" should be

**Build:**
1. **Settings → Google Calendar → Links**, shown when Check says Public:
   - **Subscribe**: `https://calendar.google.com/calendar/r?cid={id}` (fact 35, unconfirmed)
   - **Public page**: `https://calendar.google.com/calendar/embed?src={id}&ctz={zone}`
   - **iCal address**: `https://calendar.google.com/calendar/ical/{id}/public/basic.ics`

   Each has **Copy**. These are the links an operator can safely hand to members, a website or a directory. They show only what is on the Google calendar, and per decision 1 that never includes members-only events. Google members see changes as soon as Modbot writes them.
2. **Calendar page → feed row:** an **Add to Google Calendar** button beside **Copy**. It opens `https://calendar.google.com/calendar/r?cid=` plus the feed's webcal address, for staff adding the full, secret feed to their own Google account. That needs a computer (fact 32). Google re-reads it on its own schedule (fact 33).

**Don't build:**
- **Per-event template links** (`action=TEMPLATE`). Google has no official page for them, they ignore repeats (fact 34), they make a frozen copy that never updates or cancels, and Modbot has no public event page to put them on. The one place a member sees an event, the Discord card, could link to the Google event's `htmlLink` later.
- **Handing out the secret feed.** It still lists every Scheduled or Open event, members-only ones included.

**Left for the user:** whether Modbot should have a separate **public-only feed** (the open question in the where-to-advertise report). A public Google calendar covers much of that need, but this design doesn't decide it.

### 3.10 Privacy: what leaves the server

| Sent | To | When |
|---|---|---|
| A signed request naming Modbot's Google address and the two scopes | oauth2.googleapis.com | Check, and about once an hour while sending |
| Calendar ID | www.googleapis.com | Check and every write |
| Title, description, start, end, time zone, repeat and cancelled dates, a date's own times and words, world name, world page link, Modbot's event id (private to the calendar's own copy) | www.googleapis.com, then everyone the owner shares the calendar with, and the public if it's public | Only for ticked events that pass decision 1 |

**Never sent:** member names, VRChat or Discord user ids, head counts, the join link and Modbot's public address (decision 4 A), the cover picture, moderation data, any other setting. The key's private part never leaves the server; only signatures made with it do.

**Notes for the docs:**
- A public Google calendar can be found in search once embedded on a searchable site (fact 31).
- Google keeps deleted events as cancelled entries for a while (fact 3).
- The key is as strong as the database: `security.mdx`'s "Secrets stored in the database" says so for every secret.
- Whoever holds the key file can change that calendar.

**Docs to update:**
- `docs/content/docs/privacy.mdx` → "What leaves your server": a Google Calendar bullet with this table.
- `docs/content/docs/security.mdx` → "Secrets stored in the database": the key. Also how to remove it in Google Cloud.
- `docs/content/docs/moderation/calendar.mdx`: chip table, Not set up table, Preview, a "Google Calendar" section (what goes, members only, cancel, one-date changes, edits on Google are replaced), Health, and the feed's **Add to Google Calendar** button.
- A new `docs/content/docs/moderation/google-calendar.mdx` (and `meta.json`):
  - make a Google Cloud project;
  - enable the Calendar API;
  - create a service account and a JSON key;
  - make or pick a calendar;
  - Settings and sharing → "Share with specific people" → paste Modbot's Google address → "Make changes to events";
  - copy the Calendar ID from "Integrate calendar";
  - Check;
  - optionally "Make available to public", then the Links;
  - Workspace accounts: an admin may block external sharing, or key creation (fact 24).
- `docs/content/docs/not-built-yet.mdx`: pictures on Google, reading changes back from Google, per-event add links, OAuth.
- `docs/openapi/modbot.json` (regenerated).
- Specs: this document; calendar spec §3 (new place), §2.2 (row removal now asks every place), §8 facts, §9 (third loop), §14.2/§14.3.

### 3.11 Code layout

- `src/Modbot.Core/Google/`:
  - `GoogleKeyFile` (parse, refuse)
  - `GoogleSignIn` (JWT, token cache, `IModbotClock`)
  - `GoogleCalendarClient` (eventsList, aclList, get, insert, update, delete, instances; plain `HttpClient` + System.Text.Json)
  - `GoogleErrors` (classify by status plus `error.errors[0].reason`)
  - `GoogleEventIds`
- `src/Modbot.Core/Calendar/`:
  - `CalendarGoogle` (`Wants`, members-only rule)
  - `CalendarGoogleBody`
  - `CalendarGooglePublisher` + `CalendarGoogleService` (hosted, 20 s), registered in `src/Modbot.Api/ApiSurface.cs`
  - `CalendarDates` (Google-aware forget rule)
  - `CalendarFeedWriter` (shared EXDATE/time helper)
- `src/Modbot.Api/Features/Settings/GoogleCalendarSettingsEndpoints.cs`: `GET/PUT/DELETE /api/settings/google-calendar`, `POST …/check`, `POST …/add-all`, `POST …/remove-events`, all Manage settings and audited.
- `CalendarReadiness`, `CalendarContracts` (`PublishToGoogle`, `ready.google`, preview view), `CalendarPreviews`, `CalendarEndpoints` (Apply, try-again place).
- Web:
  - `components/settings/google/GoogleCalendarSection.tsx`
  - `lib/integrations.ts`, `pages/Integrations.tsx`, `pages/Settings.tsx`
  - `lib/calendarPlaces.ts`, `components/calendar/CalendarEventForm.tsx`, `EventPreview.tsx`, `EventDetails.tsx`
  - `pages/Calendar.tsx` (feed button), `components/factSentence.tsx`
- `LogSecrets`: add `assertion` and `jwt`.

### 3.12 Tests (written, not run; the Sonnet testing pass runs them)

- **Core unit:**
  - JWT: make a test key with `RSA.Create(2048)` + `ExportPkcs8PrivateKeyPem`, sign, check the signature with the public half, and check the claims come from the clock.
  - Key file: refuses wrong `type` or missing fields; `token_uri` is ignored.
  - Event id: alphabet, length, turn.
  - Body:
    - weekly on days, every 2 weeks (`WKST`), until, count, monthly on the 31st;
    - a DST-changing zone;
    - UTC with no TZID;
    - EXDATE lines;
    - HTML escaping;
    - members-only;
    - fingerprint leaves out Open state.
  - Error classification: each 403 reason, 429, 409, 404, 410, 401, `invalid_grant`.
  - `CalendarDates.CanForget` with Google holding a date.
  - `LogSecrets` catches `assertion`.
- **Loop against a fake `HttpMessageHandler`:**
  - insert timeout → next pass GET 200 ours → Published, no second POST;
  - GET 404 → POST again with the same id;
  - 409 → GET ours → adopt then PUT;
  - 409 ours-cancelled → new turn;
  - 409 not ours → Failed;
  - 429 and 403 `rateLimitExceeded` → no request of any kind until `GoogleStoppedUntil`;
  - 401 → one new token;
  - a moved date → instances lookup + PUT; after a series PUT, changed dates are sent again;
  - chip off → DELETE, then 404 → Removed;
  - calendar id changed → DELETE old, POST new;
  - the VRChat publisher doesn't drop a plain date row Google still holds.
- **Data (Testcontainers):** the migration, and the new columns round-trip.
- **API:**
  - settings never return the key;
  - Check makes only the token POST and two GETs (the handler fails any other method);
  - a bad file gives 400;
  - the preview returns the Google body;
  - readiness;
  - try-again for `googleCalendar`;
  - Add all skips members-only events.
- **Web (node runner):** `integrations()` Google states; `calendarPlaces` Google chip and `isSetUp`.
- **By hand with a throwaway Google account (testing pass):**
  1. Make a new Gmail account and a project.
  2. Enable the Calendar API.
  3. Create a service account and a key.
  4. Make a calendar named "Modbot test".
  5. Check before sharing → "Modbot can't see this calendar."
  6. Share as "See all event details" → "Modbot can only read this calendar."
  7. Share as "Make changes to events" → Can change events: Yes. Make it public → Public: Yes, and the Links appear.
  8. Schedule a one-off event and a weekly every-2-weeks event with an end date. Compare Google's grid with Modbot's, across a DST change.
  9. Cancel one date and move another. Then move the whole series an hour later and check both still hold.
  10. Drop the answer to an insert (a test-only switch) and confirm exactly one event.
  11. Delete the event on Google, then edit it in Modbot, and confirm it comes back.
  12. Cancel under decision 3.
  13. Remove sharing → Failed. Delete the key in Cloud → "Google did not accept the key."
  14. Remove Modbot's events → the calendar is empty.
  15. Subscribe to the public iCal address from another account and note how long a change takes to show.

## 4. Build steps

Each step pushes to `staging` after `dotnet build -c Release`, the web checks, and `dotnet ef migrations has-pending-model-changes`.

**Step 1: Key and Check (S, 3–4 days).**
1. `GoogleKeyFile`, `GoogleSignIn`, `GoogleCalendarClient` (read calls only), `GoogleErrors`.
2. Settings columns and migration. The settings endpoints with Check, audited.
3. Settings topic, Integrations card, `LogSecrets`.
4. The Links (B part 1).
5. The security docs section and the how-to page.

Ships on its own: an operator can set up and check the connection, and get public links for a calendar they fill by hand.

**Step 2: Sending events (M, about 1 week).**
1. `PublishToGoogle`, the place, the `calendar_date_change` columns, migration, contracts.
2. `CalendarGoogle.Wants`, `CalendarGoogleBody`, the shared EXDATE helper.
3. `CalendarGooglePublisher` and service: §3.4 to §3.7, facts, try-again.
4. The `CalendarDates` change, with the VRChat publisher's row removal moved onto it.
5. Readiness, Health, the event's box, Add all, Remove Modbot's events.

**Step 3: Form, preview, feed button, docs (S, 3–4 days).**
1. The chip, Visible to moved, Members only.
2. Preview card.
3. **Add to Google Calendar** on the feed row (B part 2).
4. Decision 3 B's cancel marking, if chosen (+2 days).
5. Calendar, privacy and not-built-yet docs, OpenAPI, spec.

**Then** one testing pass (Sonnet): every suite, the web checks, and the throwaway-account run in §3.12.

**Total: M**, about 2 to 2½ weeks, or about 2½ with decision 3 B.

## 5. Decisions (owner, 2026-10-03)

1. **What happens to members-only ("Visible to: Group") events?** A: never sent; the chip shows Members only · B: sent as private, so a public calendar shows only a busy block · C: sent only while Check says the calendar isn't public. **Recommended: A.** It is the same rule as Bluesky's decision 2. B still tells the public when the group meets, and C depends on a public setting that can change between Checks.
2. **Whose calendar does Modbot write to?** A: a calendar the server owner owns, shared with Modbot's Google address · B: a calendar the service account makes itself and makes public. **Recommended: A.** The owner keeps control and can make it public or private in Google. B needs the broad `calendar` scope and ACL writes, and leaves a calendar nobody can sign in to own, which dies with the Cloud project.
3. **What happens on Google when an event is cancelled?** A: removed, like VRChat's calendar · B: kept with "Cancelled: " in front of the title until a day after its date, then removed. **Recommended: B.** On 2026-10-01 the feed was changed to keep cancelled events for the same reason: an event that just disappears tells nobody. Costs about 2 more days.
4. **Which links go on a Google event?** A: the VRChat world page only · B: also Modbot's join address, working only while the instance is open and only when Who can join = Anyone. **Recommended: A.** B puts Modbot's public address on a public calendar and shows a dead link most of the time.
5. **What should "Add to Google Calendar" (B) be?** A: the Google calendar's own Subscribe, Public page and iCal links in Settings, plus a staff-only button for the secret feed · B: A plus per-event template links on the Discord card · C: nothing beyond A's calendar. **Recommended: A.** Template links ignore repeats and never update.
6. **Should the chip start on for new events once Google is set up?** A: yes, for new events and ones read in from VRChat (members-only excepted), plus an **Add all** button for existing ones · B: off; tick each event. **Recommended: A**, the same as the VRChat calendar and Discord event chips.
7. **What does turning Sending off do?** A: stops sending and leaves events on Google, with a separate **Remove Modbot's events** button · B: off also removes them. **Recommended: A.** Off is often temporary, and a public calendar emptied by accident is hard to undo.
8. **Should Featured events get a colour on Google?** A: no colour · B: one colour for Featured. **Recommended: A.** Whether subscribers see event colours is unconfirmed.

The owner took the recommended answer to all eight on 2026-10-03: **1 A** (members-only events are
never sent), **2 A** (the owner's own calendar, shared with Modbot's Google address), **3 B** (a
cancelled event is kept as "Cancelled: …" until a day after its date, then removed), **4 A** (only
the world page link), **5 A** (the calendar's own Subscribe, Public page and iCal links, plus a
staff-only button for the secret feed), **6 A** (the tick on by default, with **Add all**), **7 A**
(Sending off leaves events on Google; **Remove Modbot's events** is separate), **8 A** (no colour for
Featured).

The public-only feed question was settled separately: Modbot has had its own public feed at
`/api/calendar/public.ics` since 2026-10-03 (calendar design §6.1). It lists the public events planned
in Modbot; the Google links list what is on the Google calendar. The how-to page says both.

## 6. Not checked

- **Live behaviour of the fixed-id protection.** That a lost-answer insert followed by GET and then insert never doubles, and that 409 shows up reliably for a sequential retry. Google's own page says it can't guarantee catching the duplicate.
- That a deleted event's id gives 409 on insert, and whether a PUT with `status: confirmed` brings it back (the design avoids depending on it).
- Writing without domain-wide delegation to a calendar shared with a service account (widely reported; no Google page says so). That "Make changes to events" = `writer`.
- That `EXDATE;TZID=…` lines are taken in `recurrence`, and how `COUNT` counts them. Whether an `EXDATE` hides a date already changed on its own.
- What happens to changed dates when the series' time or rule changes.
- Instance id shape. `events.instances` with `originalStart` on a moved date.
- How a `private` event shows on a public calendar. Whether event colours reach subscribers.
- How Google shows plain-text descriptions with line breaks and escaped characters.
- How often Google re-reads URL-subscribed calendars (no Google number found).
- Whether `cid=` takes a webcal address and a raw calendar id, and the exact "Get shareable link" format.
- Whether the Workspace "calendar use limits" apply to personal accounts.
- That enabling the API needs no billing account. What the "later in 2026" charges will look like.
- Google disabling leaked keys automatically (blog title only).
- That `calendar.acls.readonly` works for a `writer` service account.
- Newtonsoft.Json under `Google.Apis` (from memory).
- No Google account signed in, no write API called, no code run.

## 7. Step 1 as built (2026-10-03)

What step 1 built, and where it differs from the sections above.

- **Code.** `src/Modbot.Core/Google/`: `GoogleKeyFile` (parse and refuse; `token_uri` never read),
  `GoogleSignIn` (RS256 JWT with `RSA` alone; times from `IModbotClock`; token kept until 5 minutes
  before its end; at most one token request a minute per key, a refusal handed back inside that
  minute), `GoogleCalendarClient` (`events.list` and `acl.list` only; the calendar id escaped with
  `Uri.EscapeDataString`, an id of only dots refused), `GoogleErrors` (§3.7's sorting, and the
  sentences), `GoogleCalendarIds` (an id from a pasted link; the three public links). The named
  client `google` follows no redirects, uses no proxy and caps answers at 1 MB.
- **Settings row.** The §3.1 columns, less `GoogleSendingOn`, which comes with step 2's Sending
  switch, so step 1 shows no switch that does nothing. `GoogleCanChange` is a plain bool, read only
  while `GoogleCheckedAt` is set. `GoogleProblem` holds the sentence the operator reads.
  `GoogleStoppedUntil` is kept on Forget: a limit is Google's, and a key put back must not cut it.
- **Endpoints.** `GET/PUT/DELETE /api/settings/google-calendar` and `POST …/check`, Manage settings.
  `PUT` takes the key file's text, the calendar id (or a pasted link), or both; saving either clears
  what Check found. Every change is a `settings.changed` entry for `googleCalendar`: `googleKey` as a
  secret, `googleCalendarId` as a field, and a Check's findings (`checkedCalendarName`,
  `checkedCanChangeEvents`, `checkedPublic`, `checkedProblem`) when they differ from the last.
- **Check** works on the saved key and calendar (not the form's, unlike AI's Test), so what it found
  is about what is stored. It asks for a fresh token (still at most one a minute). A 403 on the
  sharing list leaves **Public** unknown and is not a problem; a limit on any of the three calls stops
  every call until `GoogleStoppedUntil` (15 minutes, Retry-After when later, 6 hours for
  `quotaExceeded`). A Check pressed before then sends nothing and changes nothing. A `freeBusyReader`
  role reads as "Modbot can't see this calendar."
- **Links** are shown only when **Public** is **Yes** (`reader` for everyone): a free/busy-only
  calendar's links would show busy blocks and nothing else.
- **Web.** Settings topic **Google Calendar** (`#google`, Connections, after Discord) with three
  cards: **Key** (Choose key file, Forget, Modbot's Google address with Copy), **Calendar** (Calendar
  ID, Save, Check, what Check found), **Links**. The Integrations card's parts are **Key · Calendar**
  until step 2 adds **Sending**; its status is Needs setup, Working or Failed (Off comes with
  Sending).
- **Docs.** `moderation/google-calendar.mdx` (the how-to), `security.mdx` (the key, and removing
  it), `privacy.mdx` and `PRIVACY_POLICY.md` (what Check sends).

## Sources (accessed 2026-10-02/03)

- Events insert: https://developers.google.com/workspace/calendar/api/v3/reference/events/insert · resource: …/v3/reference/events · update: …/events/update · delete: …/events/delete · list: …/events/list · instances: …/events/instances · acl.list: …/v3/reference/acl/list · calendars.get: …/calendars/get · calendarList.get: …/calendarList/get
- Recurring events: https://developers.google.com/workspace/calendar/api/guides/recurringevents · Errors: …/guides/errors · Quota: …/guides/quota · Sharing: …/concepts/sharing
- Service-account OAuth: https://developers.google.com/identity/protocols/oauth2/service-account · Token expiry: https://developers.google.com/identity/protocols/oauth2#expiration
- Keys: https://docs.cloud.google.com/iam/docs/keys-create-delete · Secure-by-default orgs: https://cloud.google.com/resource-manager/docs/secure-by-default-organizations
- Use limits: https://knowledge.workspace.google.com/admin/calendar/avoid-calendar-use-limits
- Public calendar: https://support.google.com/calendar/answer/37083 · Subscribe: https://support.google.com/calendar/answer/37100
- Template links: https://github.com/InteractionDesignFoundation/add-event-to-calendar-docs/blob/main/services/google.md
- Library: https://www.nuget.org/packages/Google.Apis.Calendar.v3
- Community: https://medium.com/product-monday/accessing-google-calendar-api-with-service-account-a99aa0f7f743 · https://github.com/spatie/laravel-google-calendar/discussions/253 · https://cli.nylas.com/guides/google-calendar-api-error-codes
- Modbot sources read (worktree at `bd48f775`): `CLAUDE.md`; `.agent/specs/2026-09-15-modbot-calendar-design.md` (§2.2, §3, §5, §6, §8, §9, §14, §17); `src/Modbot.Core/Data/Entities/CalendarEvent.cs`; `CalendarCoverPicture.cs`; `src/Modbot.Core/Calendar/CalendarFeedWriter.cs`, `CalendarDates.cs`, `CalendarFingerprint.cs`, `CalendarRepeat.cs` (signatures); `src/Modbot.VRChat/Calendar/CalendarVRChatPublisher.cs` (1–1019); `src/Modbot.Discord/Calendar/CalendarDiscordPublisher.cs` (header, `ShortJoinAddress`); `src/Modbot.Api/Features/Calendar/CalendarReadiness.cs`, `CalendarEndpoints.cs` (cover, feed, join), `CalendarContracts.cs`, `CalendarPreviews.cs`; `src/Modbot.Api/Features/Onboarding/Integrations/IntegrationsHandler.cs`; `src/Modbot.Api/Features/Settings/AiSettingsEndpoints.cs`; `src/Modbot.Core/Security/ISecretProtector.cs`; `src/Modbot.Core/Logging/Store/LogSecrets.cs`; `src/Modbot.Core/Net/PictureLinks.cs`; `src/Modbot.Web/src/lib/integrations.ts`, `lib/calendarPlaces.ts`, `pages/Settings.tsx`, `pages/Calendar.tsx`; `docs/content/docs/moderation/calendar.mdx`; `Directory.Packages.props`.

