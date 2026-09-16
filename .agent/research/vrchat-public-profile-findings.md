# Get User and Get Public Profile — what each one carries

- **Source:** the VRChat API specification **v1.21.0** (released 2026-09-16), schemas
  `openapi/components/schemas/User.yaml` and `PublicProfile.yaml` at that tag, plus its release
  notes and the published pages on <https://vrchat.community>. Read on 2026-09-16. The earlier
  version of this note was written on 2026-09-15 against the SDK's 2.20.9 models and is superseded:
  the line between the two calls moved.
- **The SDK caught up on 2026-09-16.** `VRChat.API` **2.21.0** is generated from specification
  v1.21.0 and Modbot pins it. The `User` model has lost the C# properties for the fields VRChat
  no longer sends, so reading them is now a compile error rather than a rule to remember (§3).
  `GroupMember.MembershipStatus` became optional in the same release; the member sync stores
  nothing when it is absent.
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

The maintainer reported on 2026-09-15 that **bio and similar fields stopped coming back from Get
User** on live responses, while the public profile carried them. At the time the published schema
still listed `bio` on Get User, so it read as a behaviour change.

Specification v1.21.0 settles it and goes further: the fields are **gone from the schema**, not
merely blank on the wire. In the release's own words, VRChat "put a user's profile behind
`getPublicProfile`". Nine fields left the user object, five of which Modbot stores.

The rule that fell out of the first report still holds and matters more now: **a call may only
overwrite the fields its response actually carries.** A field absent from a body keeps whatever is
stored. See §4.

---

## 2. Field by field

`•` present, `—` absent, `self` present only on the owner's own profile read with `asSelf`.

| Wire field | Get User | Public profile | Notes |
|---|:--:|:--:|---|
| `id` | • | • | the key on both |
| `displayName` | • | • | |
| `pronouns` | • | • | |
| `ageVerificationStatus` | • | • | same wire values on both |
| `ageVerified` | • | • | |
| `bannerColor`, `bannerType`, `bannerUrl` | • | • | |
| `iconUrl`, `iconFrame` | • | • | the VRC+ user icon |
| `nameplateEffect`, `profileEffect` | • | • | |
| `isEconomyCreator` | • | • | |
| `bio` | — | • | **left Get User in v1.21.0** |
| `bioLinks` | — | • | left Get User; not stored as a column |
| `badges` | — | • | left Get User; not stored as a column |
| `userIcon` | — | self | left Get User; owner's own read only |
| `profilePicOverride` (+ `…Thumbnail`) | — | — | **left Get User and is on neither call** |
| `currentAvatarImageUrl` | — | self | **left Get User**; owner's own read only |
| `currentAvatarThumbnailImageUrl` | — | self | same |
| `currentAvatarTags` | — | self | same |
| `status` | • | self | Get User, for other people |
| `statusDescription` | • | self | Get User, for other people |
| `date_joined` | • | — | **Get User only** |
| `tags` | • | — | **Get User only** — the full list, including `system_troll` |
| `last_platform`, `last_mobile`, `platform` | • | — | |
| `last_login`, `last_activity`, `state` | • | — | never stored |
| `location`, `worldId`, `instanceId`, `travelingTo*` | • | — | never stored (instance secrets) |
| `note`, `friendKey`, `isFriend`, `friendRequestStatus` | • | — | never stored |
| `developerType`, `allowAvatarCopying` | • | — | |
| `acceptedTOSVersion`, `acceptedPrivacyVersion` | • | — | |
| `accountDeletionDate`, `accountDeletionLog`, `appleDetails` | • | — | |
| `trustTags` | — | • | **public profile only** — the trust-rank subset of `tags` |
| `languages` | — | • | **public profile only** |
| `representedGroup`, `groups` | — | • | **public profile only** |
| `hasVrcPlus` | — | • | **public profile only** |
| `publicWorlds`, `totalPublicWorldsCount`, `worldFavoriteLists` | — | • | **public profile only** |
| `backgroundType`, `backgroundTextureId`, `themeId`, `theme*Color` | — | • | **public profile only** |
| `themes`, `backgroundGradient*`, `backgroundTemplateId`, `bannerCustomUrl`, `iconType` | — | self | owner's own read only |

Everything the public profile adds beyond Get User is kept in `raw_public_profile` and nowhere
else, so it is available without a second fetch if a screen ever wants it.

### `asSelf`

`getPublicProfile` gained an `asSelf` parameter in v1.21.0. It returns the owner's own view of
their profile — the `self` rows above — and **VRChat ignores it on anybody else's profile**, so it
can never fill those fields for a member. SDK 2.21.0 generates it as
`GetPublicProfileWithHttpInfoAsync(string, bool? asSelf, bool? withGroupsAndWorlds, CancellationToken)`;
Modbot leaves both flags unset and names the cancellation token, since a token passed by position
would land on `asSelf`. `withGroupsAndWorlds` adds the person's groups, public worlds and world
favourite lists to the body -- nothing Modbot stores, and more bytes per read.

Nothing in Modbot wants either flag. The connection check reads no fields at
all — it asks Verify Auth Token, then Get User, then Get Group, and only looks at the status codes
— and the account Modbot is signed in as is named by Get Current User, which still carries the id,
the display name and the current avatar.

## 3. Which call fills which stored column

| Column | Filled by | Why |
|---|---|---|
| `display_name` | public profile, and Get User | both carry it |
| `bio` | **public profile** | not on Get User at all any more |
| `pronouns` | public profile, and Get User | both carry it |
| `age_verification_status`, `age_verified` | **public profile**, and Get User | both still carry it, so the sticky 18+ flag is fed by the frequent call |
| `status` | **Get User** | on the public profile for the owner only |
| `status_description` | **Get User** | same |
| `date_joined` | **Get User** | not on the public profile; never changes |
| `tags` | **Get User** | the public profile's `trustTags` is a different, smaller field and is not written here |
| `last_platform` | **Get User** | not on the public profile |
| `current_avatar_image_url` | **nothing, since v1.21.0** | left Get User; on the public profile for the owner only |
| `current_avatar_thumbnail_image_url` | **nothing, since v1.21.0** | same |
| `profile_picture_url` (`profilePicOverride`) | **nothing, since v1.21.0** | the field is on neither call |
| `raw_profile` | **Get User** | the user object as it arrived, minus the fields Modbot never keeps |
| `raw_public_profile` | **public profile** | the profile body as it arrived |

Two raw columns rather than one, so neither call erases the other's copy.

**The three picture columns now have no source.** They keep whatever was last stored, by the rule
in §4, and a person first seen after this change never gets one. The only picture either call still
carries for another person is the public profile's `iconUrl` — the VRC+ user icon, a different
field — and it is deliberately **not** written to `profile_picture_url`, in the same way
`trustTags` is deliberately not written to `tags`: mixing them would make every alternation between
the two calls look like a profile change and would write a change fact for something nobody
changed. Whether Modbot should keep `iconUrl` in a column of its own, so member lists and case
files have a face again, is a design question for the maintainer and is not decided here.

**Modbot must not read the lost fields off the SDK's `User`.** Under 2.20.9 the model still had
`Bio`, `ProfilePicOverride` and `CurrentAvatar*` properties, and a body that still sent
`"bio": ""` out of habit would wipe the bio the public profile had just filled in, once a week,
for everybody. `VRChatUserSnapshot.Fields.OnUser` therefore lists only what the user object
carries, and `From(User)` reads nothing else; 2.21.0 removed the properties, so the compiler
now holds that line too.

## 4. Absent is not empty

A response overwrites a column only when the body it arrived in **has that key**. Presence is read
from the raw JSON, not from the typed object: the SDK's generated models give a missing string the
value `""`, which is indistinguishable from a bio somebody cleared.

- present and empty → the column is cleared (somebody did clear their bio);
- absent → the column keeps what it had.

Presence is also bounded by what the call is known to carry, so a field that has left a call cannot
be written by it even if the body still mentions it. That is the part §3 relies on.

When there is no body at all — a gate that had nothing to hand over — the snapshot falls back to
everything that call can carry. That is the pre-existing behaviour and is only reachable in tests.

## 5. How often each one runs, and whether the rare read still earns its place

- **Public profile: the main read.** It runs on the existing queue and the existing schedule (user
  profile sync design §3): instance sightings first, then a moderator's click, then fact-log
  activity, then profiles older than six hours, then profiles never fetched.
- **Get User: rarely.** Once on first sight, then **once every seven days** per person, plus
  whenever something specifically needs a field only it carries.

**It still earns its place, and for a shorter list of reasons than before.** v1.21.0 took the bio
and the pictures off it, but it did not touch the five things it alone answers for, and there is no
other call that answers for them:

- `date_joined` — how old the account is, which moderators read and which never changes;
- `tags` — the trust rank, `system_troll` and the staff tags, on a scale of weeks;
- `statusDescription` and `status` — the status line a moderator reads on a profile;
- `last_platform` — desktop or headset.

If the weekly read stopped: account age and the tag list would freeze at whatever was last stored
and would never be filled for anybody first seen afterwards, so the profile card, the case-file
profile snapshot and the Chat people tool would all lose them; the status line would go the same
way; and the 404 rule in §6 would lose its second opinion, leaving a person counted as gone on the
public profile's word alone. Nothing in Modbot *decides* anything from these fields today, but they
are most of what a moderator looks at when they open somebody, and they cost almost nothing: a
10,000-person group is 10,000 ÷ 7 ≈ 1,430 Get User calls a day, about 0.017 req/s against a
3.5 req/s budget.

Age verification — the field that would have forced a frequent Get User — is still on **both**, so
the sticky 18+ flag is fed at the public profile's rate and does not wait a week.

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

The first two questions this note asked on 2026-09-15 are answered: `bio` has left the user object
outright, and `statusDescription` has not — it stays, and Get User is still the only source of it
for another person. What is left to check:

1. **Whether Get User's body still mentions `bio` or the pictures at all.** The schema says no. If
   it does, Modbot now ignores them either way (§3), and that is the intended behaviour.
2. **Whether the public profile 404s for a deleted account** the same way Get User does.
3. **That `users.profile` sustains 3.5 req/s without a 429**, and that a 429 there does not arrive
   together with one on `users.read` — if the two always fail together they are one limit wearing
   two names, and the separate budget is not buying what it was meant to.
4. **Whether `iconUrl` is filled for ordinary people or only for VRC+ subscribers.** It decides
   whether it is worth storing as the picture Modbot shows.
