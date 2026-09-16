# Get User and Get Public Profile — what each one carries

- **Source:** the `VRChat.API` SDK 2.20.9 models (`VRChat.API.Model.User`,
  `VRChat.API.Model.PublicProfile`), read by reflection and against the SDK's XML documentation,
  plus the published schemas on <https://vrchat.community>, on 2026-09-15.
- **Endpoints:**
  - `GET /users/{userId}` → `IUsersApi.GetUserWithHttpInfoAsync(userId)`. Class `users.read`.
  - `GET /profile/{userId}` → `IUsersApi.GetPublicProfileWithHttpInfoAsync(userId)`. Class
    `users.profile`. **Rate limit: the same as `users.read`, its own budget** — the maintainer's
    answer on 2026-09-15 to spec §4.3.4's standing question.
  - `GET /users/{userId}/profile` (Get Private Profile) is the **signed-in account's own** profile
    and is never used for other people.
- **Companions:** `vrchat-user-object-findings.md` (what the user object carries and what Modbot
  drops from it), `vrchat-sdk-findings.md` §5.

---

## 1. Why this note exists

The maintainer reported on 2026-09-15 that **bio and similar fields are no longer coming back from
Get User** on live responses, while the public profile carries them. The published schema still
lists `bio` on Get User, so this is a behaviour change rather than a schema change, and it is the
reason Modbot cannot treat either response as "the whole profile" any more.

The rule that falls out of it: **a call may only overwrite the fields its response actually
carries.** A field absent from a body keeps whatever is stored. See §4.

---

## 2. Field by field

`•` present, `—` absent.

| Wire field | Get User | Public profile | Notes |
|---|:--:|:--:|---|
| `id` | • | • | the key on both |
| `displayName` | • | • | |
| `bio` | • | • | **reported blank/absent on Get User today** |
| `bioLinks` | • | • | not stored as a column |
| `pronouns` | • | • | |
| `ageVerificationStatus` | • | • | same three wire values on both |
| `ageVerified` | • | • | |
| `badges` | • | • | not stored as a column |
| `iconUrl` | • | • | the VRC+ user icon, **not** `profilePicOverride` |
| `iconFrame` | • | • | |
| `nameplateEffect`, `profileEffect` | • | • | |
| `bannerColor`, `bannerType` | • | • | |
| `isEconomyCreator` | • | • | |
| `statusDescription` | • | — | **Get User only** |
| `status` | • | — | **Get User only** |
| `currentAvatarImageUrl` | • | — | **Get User only** |
| `currentAvatarThumbnailImageUrl` | • | — | **Get User only** |
| `profilePicOverride` (+ `…Thumbnail`) | • | — | **Get User only** |
| `date_joined` | • | — | **Get User only** |
| `tags` | • | — | **Get User only** — the full list, including `system_troll` |
| `last_platform`, `last_mobile`, `platform` | • | — | |
| `last_login`, `last_activity`, `state` | • | — | never stored |
| `location`, `worldId`, `instanceId`, `travelingTo*` | • | — | never stored (instance secrets) |
| `note`, `friendKey`, `isFriend`, `friendRequestStatus` | • | — | never stored |
| `developerType`, `allowAvatarCopying`, `currentAvatarTags` | • | — | |
| `acceptedTOSVersion`, `acceptedPrivacyVersion` | • | — | |
| `accountDeletionDate`, `accountDeletionLog`, `appleDetails` | • | — | |
| `bannerUrl`, `userIcon` | • | — | |
| `trustTags` | — | • | **public profile only** — the trust-rank subset of `tags` |
| `languages` | — | • | **public profile only** |
| `representedGroup` | — | • | **public profile only** — id, name, icon, banner |
| `hasVrcPlus` | — | • | **public profile only** |
| `backgroundType`, `themeId` | — | • | **public profile only** |

**Nothing Modbot stores in a column is carried by the public profile alone.** Everything the public
profile adds beyond Get User (`trustTags`, `languages`, `representedGroup`, `hasVrcPlus`, the theme
fields) is kept in `raw_public_profile` and nowhere else, so it is available without a second fetch
if a screen ever wants it.

## 3. Which call fills which stored column

| Column | Filled by | Why |
|---|---|---|
| `display_name` | public profile, and Get User | both carry it |
| `bio` | **public profile** | Get User no longer returns it reliably |
| `pronouns` | public profile, and Get User | both carry it |
| `age_verification_status`, `age_verified` | public profile, and Get User | both carry it, so the sticky 18+ flag is fed by the frequent call |
| `status` | **Get User** | not on the public profile |
| `status_description` | **Get User** | not on the public profile |
| `current_avatar_image_url` | **Get User** | not on the public profile |
| `current_avatar_thumbnail_image_url` | **Get User** | not on the public profile |
| `profile_picture_url` (`profilePicOverride`) | **Get User** | the public profile's `iconUrl` is a different field and is not written here |
| `date_joined` | **Get User** | not on the public profile; never changes |
| `tags` | **Get User** | the public profile's `trustTags` is a different, smaller field and is not written here |
| `last_platform` | **Get User** | not on the public profile |
| `raw_profile` | **Get User** | the user object as it arrived, minus the fields Modbot never keeps |
| `raw_public_profile` | **public profile** | the profile body as it arrived |

Two raw columns rather than one, so neither call erases the other's copy. `raw_profile` keeps the
meaning it has always had — the Get User body — and the new column is the new body.

`iconUrl` is deliberately **not** mapped onto `profile_picture_url`, and `trustTags` is deliberately
**not** mapped onto `tags`. Mixing them would make every alternation between the two calls look like
a profile change and would write a change fact for something nobody changed.

## 4. Absent is not empty

A response overwrites a column only when the body it arrived in **has that key**. Presence is read
from the raw JSON, not from the typed object: the SDK's generated models give a missing string the
value `""`, which is indistinguishable from a bio somebody cleared.

- present and empty → the column is cleared (somebody did clear their bio);
- absent → the column keeps what it had.

When there is no body at all — a gate that had nothing to hand over — the snapshot falls back to the
SDK's own serialisation of the typed object, which emits every property, so every field that call
carries counts as present. That is the pre-existing behaviour and is only reachable in tests.

## 5. How often each one runs

- **Public profile: the main read.** It runs on the existing queue and the existing schedule (user
  profile sync design §3): instance sightings first, then a moderator's click, then fact-log
  activity, then profiles older than six hours, then profiles never fetched.
- **Get User: rarely.** Once on first sight, then **once every seven days** per person, plus
  whenever something specifically needs a field only it carries.

Seven days is chosen from the table above. Of the fields only Get User carries:

- `date_joined` never changes after the account is made;
- `tags` (trust rank, `system_troll`, staff tags) moves on a scale of weeks;
- `last_platform`, `status` and the avatar urls change constantly but nothing in Modbot decides
  anything from them;
- `status_description` is the one genuinely moderator-facing field that goes stale, and it is a
  line of self-description, not a signal anything acts on.

Age verification — the field that would have forced a frequent Get User — is on **both**, so the
sticky 18+ flag is still fed at the public profile's rate.

The cost is negligible: a 10,000-person group is 10,000 ÷ 7 ≈ 1,430 Get User calls a day, about
0.017 req/s against a 3.5 req/s budget.

## 6. A 404 from one call

The two calls can disagree. Either can 404 while the other answers, so:

- each call records its own "not found" mark and its own last error;
- `not_found_at` — the mark that means *the account is gone* and that holds a person out of the
  queue for a week — is set only when **both** calls answered 404, or when one did and the other has
  never succeeded for that person;
- a success on either call clears that call's mark, and so clears `not_found_at`.

A brand-new id that 404s on its first public-profile read is still marked immediately, because Get
User has never succeeded for it. That is the common case and it behaves as it did before.

## 7. What a live run should confirm

1. **Whether `bio` is absent from the Get User body or present and empty.** Modbot is safe either
   way (§4 keeps the stored value only in the first case, and an empty bio is a real edit in the
   second), but it decides whether Get User can still be trusted for bio at all.
2. **Whether `statusDescription` is still returned by Get User**, or has gone the way of `bio`. If
   it has, the status line has no source at all and the public profile is the whole profile.
3. **Whether the public profile 404s for a deleted account** the same way Get User does.
4. **That `users.profile` sustains 3.5 req/s without a 429**, and that a 429 there does not arrive
   together with one on `users.read` — if the two always fail together they are one limit wearing
   two names, and the separate budget is not buying what it was meant to.
