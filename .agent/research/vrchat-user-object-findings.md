# VRChat user object — what the profile sync reads

- **Source:** the `VRChat.API` SDK 2.20.9 model (`VRChat.API.Model.User`) and the XML documentation it
  was generated with, read on 2026-09-13 while building the profile sync. **Not yet checked against a
  live response** — see §5 for what a live run should confirm.
- **Endpoint:** `GET /users/{userId}` → `IUsersApi.GetUserWithHttpInfoAsync(userId)`. Class
  `users.read`, its own lane, 3.5 req/s (user profile sync design §5). "Get public user information
  about a specific user using their ID."
- **Companion:** `vrchat-sdk-findings.md` §5 (which user models carry which fields).

---

## 1. The fields Modbot keeps

| VRChat field (SDK property) | Stored as | Watched for change |
|---|---|---|
| `id` | `user_id` (text, opaque) | key |
| `displayName` | `display_name` | yes |
| `bio` | `bio` | yes |
| `statusDescription` | `status_description` | yes |
| `pronouns` | `pronouns` | yes |
| `currentAvatarImageUrl` | `current_avatar_image_url` | yes |
| `currentAvatarThumbnailImageUrl` | `current_avatar_thumbnail_image_url` | yes |
| `profilePicOverride` | `profile_picture_url` | yes — the SDK's own note: "When profilePicOverride is not empty, use it instead" |
| `date_joined` (`DateJoined`, `DateOnly`) | `date_joined` | yes |
| `tags` | `tags` (jsonb array, sorted) | yes, as a set |
| `ageVerificationStatus` | `age_verification_status` | yes |
| `ageVerified` | `age_verified` | yes |
| `status` | `status` | **no** — flips with every session |
| `last_platform` | `last_platform` | **no** |
| the whole body | `raw_profile` (jsonb) | — |

Every other field on the object — `location`, `worldId`, `instanceId`, `travelingTo*`, `state`,
`last_login`, `last_activity`, `isFriend`, `friendKey`, `friendRequestStatus`, `note`, `badges`,
`bioLinks`, `banner*`, `userIcon`, `iconUrl`, `nameplateEffect`, `profileEffect`, `platform`,
`developerType`, `allowAvatarCopying`, `isEconomyCreator`, `accountDeletion*`, `appleDetails`,
`acceptedTOSVersion`, `acceptedPrivacyVersion`, `currentAvatarTags` — is present on the raw copy
except the ones in §3.

## 2. Age verification — three fields, three values

The SDK exposes:

- `ageVerificationStatus` — enum `AgeVerificationStatus` with three members, and the wire values
  matter more than the names:

  | SDK member | Wire value | SDK's own description |
  |---|---|---|
  | `plus18` | **`18+`** | — |
  | `hidden` | `hidden` | — |
  | `verified` | `verified` | "`verified` is obsolete. User who have verified and are 18+ can switch to `plus18` status." |

- `ageVerified` (`bool`) — "`true` if, user is age verified (not 18+)."

The same two fields exist on `CurrentUser`, `LimitedUserInstance` and `PublicProfile`.

**How Modbot reads them (user profile sync design §4).** VRChat's age verification confirms
18-or-over and nothing else, so any positive signal is the same claim: a status of `18+`, the
obsolete `verified`, or `ageVerified` true all set the sticky flag. `hidden` means hidden — the
person may or may not be verified — and sets nothing. The one thing none of these can say is that a
person is *not* verified, which is the whole reason the flag is sticky.

**The status is read from the raw body, not from the enum.** An enum with one member already
obsolete is an enum that has changed once; a fourth value would fail Newtonsoft's deserialisation of
the whole object. `VRChatUserSnapshot.From` prefers `raw["ageVerificationStatus"]` as text and falls
back to the enum's `EnumMember` value only when there is no body.

## 3. Never stored, even in the raw copy

| Field | Why |
|---|---|
| `location`, `travelingToLocation`, `travelingToInstance`, `instanceId` | Instance location strings carry `~nonce(…)`, an instance secret. Foundation §5.3: Modbot never persists instance secrets. |
| `note` | The Modbot account's private note on the person. Not profile data. |
| `friendKey` | A credential-shaped value. Not profile data. |

`VRChatUserSnapshot.NeverStored` is the list; the snapshot tests prove the stored body lacks them.

## 4. Things the SDK model made awkward

- **`User` has no parameterless-constructor-safe defaults for enums.** A `new User()` has
  `AgeVerificationStatus == 0`, which is no member. Production never hits this (the object comes from
  JSON) but the fakes have to set the enums explicitly.
- **`DateJoined` is a non-nullable `DateOnly`.** A missing value arrives as `default`
  (0001-01-01) rather than null; the snapshot maps `default` to null.
- **`Tags` order is not documented as stable.** Sorted before comparison so that two responses
  listing the same tags in a different order are not a change.
- **The typed object cannot be re-serialised into what arrived.** `User.ToJson()` is Newtonsoft's
  view of the model, not the response; the gate's `RawResponse` is what goes in `raw_profile`, and
  `ToJson()` is the fallback only when there was no body.

## 5. What a live run should confirm

None of the following could be checked from the SDK alone. Each is a one-line finding to add here.

1. **What `ageVerified` returns for another user whose status is `hidden`.** If it is `true` for
   anyone who has ever verified, Modbot learns verification even when hidden and the sticky flag is
   mostly redundant; if it tracks the visible status, the flag is doing real work. Either way the
   rule above is safe; the finding decides how often the flag matters.
2. **Whether `verified` still appears at all**, or every verified user has moved to `18+`.
3. **The exact `date_joined` wire format** (the SDK says `DateOnly`; confirm it is `YYYY-MM-DD`).
4. **What a deleted account answers** — 404 is assumed (the fake returns VRChat's usual
   `{"error":{"message":"User not found","status_code":404}}` shape); a 403 or a 200 with a stub
   object would need handling.
5. **That `users.read` at 3.5 req/s sustains without a 429**, and that no 429 appears on the group
   classes shortly after profile bursts (foundation §4.2.5's withdrawal signature).
