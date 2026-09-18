import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { OtherTags, ProfileBadges } from '@/components/ProfileBadges'
import { Field } from '@/components/subject/shared'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type CurrentUser, type VRChatUserProfile } from '@/lib/api'
import { can } from '@/lib/permissions'
import { useStoredProfile, type StoredProfile } from '@/lib/useStoredProfile'
import { cn } from '@/lib/utils'

/**
 * One person's stored VRChat profile, with how old it is written next to it.
 *
 * Spec 4.2.5: freshness is visible, never implied. Everything here is shown as of
 * `lastRefreshedAt`, and a profile older than the sync's own threshold is labelled stale, because
 * "no flags found" on a bio from March is not the same claim as on a bio from an hour ago. The
 * reading and the refresh-on-open are `useStoredProfile`.
 *
 * The profile is in two parts because the person popup draws them in two places: `ProfileIdentity`
 * (picture, name, badges, freshness, the 18+ mark) on the left, and `ProfileDetails` (bio, status,
 * dates, the remaining tags) at the top of the Overview tab. One `useStoredProfile` feeds both, so
 * the refresh is asked for once and both parts move together when it lands. `UserProfileCard` is
 * the two stacked, for a page that wants the whole thing in one place.
 */

/** Both parts stacked: the whole profile in one place. */
export function UserProfileCard({ subjectId, me }: { subjectId: string; me: CurrentUser }) {
  const stored = useStoredProfile(subjectId)

  return (
    <div className="flex flex-col gap-3">
      <ProfileIdentity stored={stored} me={me} />
      <ProfileDetails stored={stored} />
    </div>
  )
}

/**
 * Who this is: picture, name, pronouns, the badge row, how old the reading is, and the 18+ mark.
 * The part that belongs beside the person wherever they are shown.
 */
export function ProfileIdentity({
  stored,
  me,
}: {
  stored: StoredProfile
  /** The signed-in account, for deciding whether to draw the flag control. */
  me: CurrentUser
}) {
  const { profile, error, refreshing, note, setProfile } = stored

  if (error) {
    return (
      <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
        {error}
      </p>
    )
  }

  if (!profile) {
    return (
      <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Loading profile…
      </p>
    )
  }

  const fetched = profile.known && profile.lastRefreshedAt

  return (
    <div className="flex flex-col gap-3">
      {fetched ? (
        <Identity profile={profile} />
      ) : (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {profile.known ? 'Profile not fetched yet.' : 'Not seen before.'}
        </p>
      )}

      <Freshness profile={profile} refreshing={refreshing} note={note} />

      <AgeVerified profile={profile} canEdit={can(me, 'EditAgeVerification')} onChanged={setProfile} />
    </div>
  )
}

/**
 * The rest of the profile: bio, status, when they joined VRChat, when Modbot last saw them, and
 * the tags that have no badge. Draws nothing until the profile is there, because the identity part
 * already says why it is not.
 */
export function ProfileDetails({ stored }: { stored: StoredProfile }) {
  const { profile, error } = stored

  if (error || !profile || !profile.known || !profile.lastRefreshedAt) return null

  return (
    <div className="flex flex-col gap-2">
      {profile.statusDescription && (
        <Field label="Status">
          {/* User-authored text, rendered as text. Never as HTML. */}
          <span className="whitespace-pre-wrap">{profile.statusDescription}</span>
        </Field>
      )}

      {profile.bio && (
        <Field label="Bio">
          <span className="whitespace-pre-wrap">{profile.bio}</span>
        </Field>
      )}

      <div className="flex flex-wrap gap-x-6 gap-y-2">
        {profile.dateJoined && <Field label="Joined VRChat">{formatDay(profile.dateJoined)}</Field>}
        {profile.lastSeenAt && (
          <Field label="Last seen by Modbot">{ago(profile.lastSeenAt, profile.now)}</Field>
        )}
      </div>

      <OtherTags tags={profile.tags} />
    </div>
  )
}

/**
 * The age of what is shown, stated in words. Never implied: the stored data is only ever as true
 * as `lastRefreshedAt`, and stale data says so in words rather than in a colour.
 */
function Freshness({
  profile,
  refreshing,
  note,
}: {
  profile: VRChatUserProfile
  refreshing: boolean
  note: string | null
}) {
  return (
    <div
      className={cn(
        'rounded-xl border px-3 py-2',
        profile.stale ? 'border-warn/40 bg-warn/10' : 'bg-muted/40',
      )}
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="flex flex-wrap items-center gap-x-2">
        <span className="font-medium">
          {profile.lastRefreshedAt
            ? `Last refreshed ${ago(profile.lastRefreshedAt, profile.now)}`
            : 'Never refreshed'}
        </span>
        {profile.stale && profile.lastRefreshedAt && (
          <span className="text-warn">
            stale, older than {Math.round(profile.staleAfterSeconds / 3600)} hours
          </span>
        )}
        {refreshing && (
          <span className="text-muted-foreground" aria-live="polite">
            refreshing…
          </span>
        )}
      </div>
      {note && <div className="mt-1 text-muted-foreground">{note}</div>}
      {profile.notFoundAt && (
        <div className="mt-1 text-muted-foreground">
          No VRChat account with this id as of {formatDay(profile.notFoundAt)}.
        </div>
      )}
    </div>
  )
}

function Identity({ profile }: { profile: VRChatUserProfile }) {
  const picture = profile.profilePictureUrl || profile.avatarThumbnailUrl

  return (
    <div className="flex gap-3">
      {picture ? (
        <img
          src={picture}
          alt=""
          className="size-16 shrink-0 rounded-full bg-muted object-cover"
          referrerPolicy="no-referrer"
        />
      ) : (
        <div className="size-16 shrink-0 rounded-full bg-muted" />
      )}

      <div className="flex min-w-0 flex-1 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium break-words" style={{ fontSize: 'var(--text-base)' }}>
            {profile.displayName ?? <span className="font-mono">{profile.userId}</span>}
          </span>
          {profile.pronouns && <span className="text-muted-foreground">{profile.pronouns}</span>}
        </div>

        <ProfileBadges tags={profile.tags} lastPlatform={profile.lastPlatform} rank={profile.trustRank} />
      </div>
    </div>
  )
}

/**
 * The sticky flag, with its source and when it was set, next to what VRChat said last.
 *
 * The two can disagree and the disagreement is the point (user profile sync design §4): a user
 * who showed 18+ once and hides it now is still 18+. Clearing it is a decision, so it asks for a
 * reason and is recorded against the account that made it.
 */
function AgeVerified({
  profile,
  canEdit,
  onChanged,
}: {
  profile: VRChatUserProfile
  canEdit: boolean
  onChanged: (next: VRChatUserProfile) => void
}) {
  const [editing, setEditing] = useState<boolean | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const flag = profile.eighteenPlus

  const submit = () => {
    if (editing === null) return
    setBusy(true)
    setProblem(null)

    api
      .setAgeVerified(profile.userId, { verified: editing, reason: reason.trim() || undefined })
      .then((next) => {
        onChanged(next)
        setEditing(null)
        setReason('')
      })
      .catch((e: unknown) =>
        setProblem(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to change this flag.'
            : 'Could not save the change.',
        ),
      )
      .finally(() => setBusy(false))
  }

  return (
    <div
      className="rounded-xl border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span
          className={cn(
            'inline-flex items-center rounded-full border px-2 py-0.5 font-medium',
            flag.verified ? 'border-transparent bg-ok/15 text-ok' : 'text-muted-foreground',
          )}
          style={{ borderWidth: 'var(--hairline)' }}
        >
          {flag.verified ? '18+ verified' : 'Not seen as 18+ verified'}
        </span>

        <span className="text-muted-foreground">
          {flag.verified && flag.source === 'vrchat' && flag.since && (
            <>first seen on VRChat {formatDay(flag.since)}</>
          )}
          {flag.source === 'manual' && flag.since && (
            <>
              {flag.verified ? 'set' : 'cleared'} by {flag.setByUsername ?? 'a moderator'} on{' '}
              {formatDay(flag.since)}
            </>
          )}
        </span>

        <span className="flex-1" />

        {canEdit && editing === null && (
          <Button variant="outline" size="xs" onClick={() => setEditing(!flag.verified)}>
            {flag.verified ? 'Clear flag' : 'Mark 18+ verified'}
          </Button>
        )}
      </div>

      <p className="mt-1 text-muted-foreground">
        VRChat shows this person as{' '}
        <span className="font-mono">{profile.ageVerificationStatusLastSeen ?? 'unknown'}</span>
        {profile.lastRefreshedAt ? ` as of ${ago(profile.lastRefreshedAt, profile.now)}` : ''}.
      </p>

      {editing !== null && (
        <div className="mt-2 flex flex-col gap-2">
          <label className="flex flex-col gap-1">
            <span className="text-muted-foreground">
              {editing ? 'Why are you marking this person 18+ verified?' : 'Why are you clearing the flag?'}
            </span>
            <input
              className="rounded-md border bg-background px-2 py-1"
              style={{ borderWidth: 'var(--hairline)' }}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="Reason"
              maxLength={500}
            />
          </label>
          <div className="flex gap-2">
            <Button size="xs" onClick={submit} disabled={busy}>
              {editing ? 'Mark 18+ verified' : 'Clear flag'}
            </Button>
            <Button size="xs" variant="ghost" onClick={() => setEditing(null)} disabled={busy}>
              Cancel
            </Button>
          </div>
          {problem && <div className="text-destructive">{problem}</div>}
        </div>
      )}
    </div>
  )
}
