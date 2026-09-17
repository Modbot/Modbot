# VRChat trust ranks — the tags, the order, the colours

- **Sources:** the community tag list at <https://vrchat.community/tags/user> (the `system_trust_*`
  descriptions and the removal dates are quoted from it), VRChat's own page
  <https://docs.vrchat.com/docs/vrchat-safety-and-trust-system> (the rank names, the Nuisance
  description and the "show as User" rule), the VRChat wiki's Trust Rank page, and VRCX's
  `src/shared/utils/userTransforms.js` and `src/stores/settings/appearance.js` (the precedence
  every third-party tool has converged on, and the colours). Read on 2026-09-16.
- **Where the tags come from in Modbot:** `GET /users/{userId}` → `tags`, stored on `vrchat_user.tags`
  by the weekly user read (`vrchat-user-object-findings.md` §1, `vrchat-public-profile-findings.md`
  §3). The public profile carries `trustTags` instead, a smaller list — see §4.
- **Not checked against a live response.** Nothing here needed a new endpoint, and none of it has
  been confirmed on the wire by Modbot. §5 lists what a live run should look at.

---

## 1. The tags

VRChat names the tags one rank *below* what they mean. The community list says so in as many words:
"trust rank tags have an offset of 1 with their name, so `system_trust_trusted` is actually only
Known User". The offset is a leftover from the 2018 rename, when the ranks were renamed and the
tags were not.

| Tag | Rank it means | Community description (verbatim) |
|---|---|---|
| *(none of the below)* | **Visitor** | Visitors have no trust tag at all |
| `system_trust_basic` | **New User** | "User is 'New User' (blue) Trust rank" |
| `system_trust_known` | **User** | "User is 'User' (green) Trust rank" |
| `system_trust_trusted` | **Known User** | "User is 'Known User' (orange) Trust rank" |
| `system_trust_veteran` | **Trusted User** | "User is 'Trusted User' (purple) Trust rank" |
| `system_trust_legend` | **Legend** (retired) | "Veteran User (gold)" — removed September 2018 |
| `system_probable_troll` | **Nuisance** | "User has been reported multiple times and is (probably) a troll" |
| `system_troll` | **Nuisance** | "User is a confirmed troll" |
| `admin_moderator` | **VRChat Team** | "User is part of the VRChat Staff team" |

Two more `system_trust_*` tags exist in old data and are **not ranks**:

| Tag | What it was |
|---|---|
| `system_trust_intermediate` | an in-between step of the old trust ladder; removed 2022-05-05 |
| `system_trust_advanced` | the same; removed 2022-05-05 |

Both were subdivisions between the named ranks (the "Basic → Intermediate → Advanced" steps
VRChat used before the 2018 rename). A user whose stored tags still carry one also carries the
real rank tag beside it, so the parser ignores them rather than guessing.

Other `system_*` tags on the same list say nothing about trust and are left alone:
`system_supporter` (an active VRC+ subscription), `system_early_adopter`, `system_no_captcha`,
`system_avatar_access`, `system_world_access`, `system_feedback_access`, and the `language_*` tags.

**Ranks are not cumulative on the wire.** A Trusted User carries `system_trust_veteran` only, not the
three below it as well. That is why the parser takes the highest tag present rather than counting.

## 2. Precedence — highest wins, and two overrides

The order Modbot uses, lowest to highest:

```
Visitor < New User < User < Known User < Trusted User < Legend
```

with two ranks that sit outside the ladder and **override** whatever ladder rank the tags also carry:

1. **VRChat Team** (`admin_moderator`) beats everything. Staff accounts carry a ladder tag too
   (VRCX's own test uses `['admin_moderator', 'system_trust_veteran']`), and a moderator should read
   as VRChat Team, not as Trusted User.
2. **Nuisance** (`system_troll` or `system_probable_troll`) beats every ladder rank. A troll carries a
   ladder tag as well — VRChat does not take it away, it marks the account on top — and a
   moderator reading a roster needs to see Nuisance, not the User rank underneath. Between the two
   nuisance tags nothing is ranked: both are the same rank.

VRChat Team over Nuisance when both are present, because it is the rarer claim and a staff account
that has been mass-reported is still a staff account. Nobody has seen both on one user; the rule is
here so the parser has an answer.

VRCX keeps the ladder rank and paints it in the override's colour; Modbot collapses the two into one
rank because a badge has one colour and one word. The ladder rank underneath is still on the row
in `tags` for anyone who wants it.

`developerType` — `internal`, `moderator` — is VRCX's second signal for staff. Modbot does not read
it: the tag list is the field Modbot already stores and diffs, and staff who hide their
`developerType` (the SDK says they can) do so on purpose.

## 3. Colours

VRChat paints the rank on the nameplate. The words in the community list — blue, green, orange,
purple, gold — are VRChat's; the hex values are VRCX's defaults, which match the in-game nameplate
colours and are what every screenshot of a VRChat rank looks like.

| Rank | Colour | Hex | Note |
|---|---|---|---|
| Visitor | grey | `#CCCCCC` | |
| New User | blue | `#1778FF` | |
| User | green | `#2BCF5C` | |
| Known User | orange | `#FF7B42` | |
| Trusted User | purple | `#8143E6` | VRCX's *current* default is the lighter `#B18FFF`, with `#8143E6` kept as a preset; `#8143E6` is the in-game purple and is the one Modbot uses |
| Legend | gold | `#FFD000` | retired; VRCX lists it among the purple presets and no longer has a Legend colour of its own |
| Nuisance | dark red | `#782F2F` | VRCX's nuisance colour; in-game the account gets a marker over the nameplate rather than a recolour |
| VRChat Team | red | `#FF2626` | the website shows staff with a red border |

Grey for Visitor is deliberate: it is the "no rank yet" colour and reads as such next to the rest.

## 4. `trustTags` on the public profile

The public profile (`GET /profile/{userId}`) carries `trustTags`, described as the trust-rank
subset of `tags`. Modbot keeps it in `raw_public_profile` and does **not** derive the rank from it,
for the reason the public-profile note gives for not writing it to `tags`: it is a different, smaller
list, and nobody has checked whether it carries `system_troll` and `admin_moderator` or only the
ladder tag. If it lacks the override tags, a Nuisance would flip to User every six hours and back to
Nuisance every week, and each flip would be a profile-change fact for something nobody changed.

So the rank is computed from `tags` and follows the weekly user read. That is the same schedule the
tag list itself has, and the rank cannot be fresher than the tags it comes from.

## 5. What a live run should confirm

1. **Whether `trustTags` carries the override tags.** If it carries `system_troll`,
   `system_probable_troll` and `admin_moderator` as well as the ladder tag, the rank could be
   refreshed on the public profile's schedule instead of weekly. One user of each kind answers it.
2. **Whether any live account still carries `system_trust_legend`.** The rank was retired in 2018;
   the tag may or may not have been stripped from the accounts that had it.
3. **Whether a staff account carries a ladder tag beside `admin_moderator`**, and which one.
4. **Whether `system_trust_intermediate` / `system_trust_advanced` still appear** on any account.
   If they never do, the note in §1 is history and the parser's silence on them costs nothing.
