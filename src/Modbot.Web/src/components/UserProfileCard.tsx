import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { useDemo } from '@/lib/demo'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type CurrentUser, type VRChatUserProfile } from '@/lib/api'
import { can } from '@/lib/permissions'
import { cn } from '@/lib/utils'

/**
 * One person's stored VRChat profile, with how old it is written next to it.
 *
 * Spec 4.2.5: freshness is visible, never implied. Everything here is shown as of
 * `lastRefreshedAt`, and a profile older than the sync's own threshold is labelled stale, because
 * "no flags found" on a bio from March is not the same claim as on a bio from an hour ago.
 *
 * Opening the card asks the server for a refresh (the "opened in Modbot" tier, behind only people
 * in an instance right now) and then polls the profile until `lastRefreshedAt` moves. Polling is
 * deliberate for this milestone: two seconds with a little backoff, giving up after a minute, no
 * realtime channel. If the refresh cannot happen -- the users lane is cold-stopped, the account is
 * gone -- the stored data stays on screen with the plain reason beside it.
 */

/** How long the card keeps asking after a refresh was queued. */
const GIVE_UP_AFTER_MS = 60_000

/** Poll delays, in order; the last one repeats. */
const POLL_MS = [2_000, 2_000, 3_000, 4_000, 6_000, 8_000]

export function UserProfileCard({
  subjectId,
  me,
}: {
  subjectId: string
  /** The signed-in account, for deciding whether to draw the flag control. */
  me: CurrentUser
}) {
  const demo = useDemo()

  const [profile, setProfile] = useState<VRChatUserProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [refreshNote, setRefreshNote] = useState<string | null>(null)

  // The lastRefreshedAt the refresh was asked against. The poll ends when the server's differs.
  const baseline = useRef<string | null>(null)
  const cancelled = useRef(false)

  const load = useCallback(() => api.userProfile(subjectId), [subjectId])

  useEffect(() => {
    cancelled.current = false
    let timer: ReturnType<typeof setTimeout> | null = null

    const fail = (e: unknown) => {
      if (cancelled.current) return
      setError(
        e instanceof ApiError && e.status === 403
          ? 'You do not have permission to view profiles.'
          : 'Could not load this profile.',
      )
    }

    const poll = (attempt: number, startedAt: number) => {
      timer = setTimeout(() => {
        load()
          .then((next) => {
            if (cancelled.current) return
            setProfile(next)

            const moved = next.lastRefreshedAt !== baseline.current
            const failed =
              next.refreshErrorAt !== null &&
              !next.refresh.pending &&
              next.lastRefreshedAt === baseline.current
            const stopped = !next.refresh.pending && next.refresh.blocked !== null
            const timedOut = Date.now() - startedAt > GIVE_UP_AFTER_MS

            if (moved) {
              setRefreshing(false)
              setRefreshNote(null)
              return
            }

            if (failed) {
              setRefreshing(false)
              setRefreshNote(`Couldn't refresh: ${next.refreshError ?? 'VRChat did not answer.'}`)
              return
            }

            if (stopped) {
              setRefreshing(false)
              setRefreshNote(`Couldn't refresh: ${next.refresh.blocked}`)
              return
            }

            if (timedOut) {
              setRefreshing(false)
              setRefreshNote('Refresh still waiting.')
              return
            }

            poll(attempt + 1, startedAt)
          })
          .catch(fail)
      }, POLL_MS[Math.min(attempt, POLL_MS.length - 1)])
    }

    // Show what is stored first, then ask for it to be brought up to date.
    load()
      .then((stored) => {
        if (cancelled.current) return
        setProfile(stored)
        baseline.current = stored.lastRefreshedAt

        // A demo has no VRChat account and never will, so there is nothing to bring the profile
        // up to date from and no refusal worth putting in front of anybody.
        if (demo) return

        if (stored.refresh.blocked && !stored.refresh.pending) {
          setRefreshNote(`Couldn't refresh: ${stored.refresh.blocked}`)
          return
        }

        return api.requestUserRefresh(subjectId).then((asked) => {
          if (cancelled.current) return

          if (asked.outcome === 'FreshEnough') {
            setRefreshNote(null)
            return
          }

          if (asked.outcome === 'NotAvailable' || asked.outcome === 'NotAPerson') {
            setRefreshNote(`Couldn't refresh: ${asked.explanation}`)
            return
          }

          setRefreshing(true)
          poll(0, Date.now())
        })
      })
      .catch(fail)

    return () => {
      cancelled.current = true
      if (timer) clearTimeout(timer)
    }
  }, [subjectId, load, demo])

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

  return (
    <div className="flex flex-col gap-3">
      <Freshness profile={profile} refreshing={refreshing} note={refreshNote} />

      {profile.known && profile.lastRefreshedAt ? (
        <Profile profile={profile} />
      ) : (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {profile.known ? 'Profile not fetched yet.' : 'Not seen before.'}
        </p>
      )}

      <AgeVerified
        profile={profile}
        canEdit={can(me, 'EditAgeVerification')}
        onChanged={setProfile}
      />
    </div>
  )
}

/**
 * The age of what is shown, stated before it. Never implied: the stored data is only ever as
 * true as `lastRefreshedAt`, and stale data says so in words rather than in a colour.
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

function Profile({ profile }: { profile: VRChatUserProfile }) {
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

      <div className="min-w-0 flex-1" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium" style={{ fontSize: 'var(--text-base)' }}>
            {profile.displayName ?? <span className="font-mono">{profile.userId}</span>}
          </span>
          {profile.pronouns && <span className="text-muted-foreground">{profile.pronouns}</span>}
        </div>

        {profile.statusDescription && (
          <div className="mt-0.5 text-muted-foreground">“{profile.statusDescription}”</div>
        )}

        {/* User-authored text, rendered as text. Never as HTML. */}
        {profile.bio && <p className="mt-2 whitespace-pre-wrap break-words">{profile.bio}</p>}

        <dl className="mt-2 flex flex-wrap gap-x-4 gap-y-0.5 text-muted-foreground">
          {profile.dateJoined && <Pair label="Joined VRChat" value={formatDay(profile.dateJoined)} />}
          {profile.lastPlatform && <Pair label="Last platform" value={profile.lastPlatform} />}
          {profile.lastSeenAt && <Pair label="Last seen by Modbot" value={ago(profile.lastSeenAt, profile.now)} />}
        </dl>

        {profile.tags.length > 0 && (
          <div className="mt-2 flex flex-wrap gap-1">
            {profile.tags.map((tag) => (
              <span
                key={tag}
                className="rounded-full border px-2 py-0.5 font-mono text-muted-foreground"
                style={{ borderWidth: 'var(--hairline)', fontSize: '0.6875rem' }}
              >
                {tag}
              </span>
            ))}
          </div>
        )}
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

function Pair({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-1">
      <dt>{label}:</dt>
      <dd className="text-foreground">{value}</dd>
    </div>
  )
}
