import { useEffect, useRef, useState } from 'react'
import { Popover } from 'radix-ui'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { OtherTags } from '@/components/ProfileBadges'
import { ProfileHeader } from '@/components/ProfileHeader'
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
 * Who this is: banner, picture, name, pronouns, the badge row, the group they represent, how old
 * the reading is, and the 18+ mark. The part that belongs beside the person wherever they are shown.
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

  const mark = <AgeMark profile={profile} canEdit={can(me, 'EditAgeVerification')} onChanged={setProfile} />

  return (
    <div className="flex flex-col gap-3">
      {fetched ? (
        <Identity profile={profile} mark={mark} />
      ) : (
        // No header to hang the mark on, so it stands on its own: the flag is known for an id
        // whose profile has never been fetched, and it is the one thing worth saying about them.
        <div className="flex flex-col gap-2">
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {profile.known ? 'Profile not fetched yet.' : 'Not seen before.'}
          </p>
          <div className="flex flex-wrap items-center gap-1">{mark}</div>
        </div>
      )}

      <Freshness profile={profile} refreshing={refreshing} note={note} />
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
        {profile.dateJoined && <Field label="Joined VRChat"><span className="font-mono">{formatDay(profile.dateJoined)}</span></Field>}
        {profile.lastSeenAt && (
          <Field label="Last seen by Modbot"><span className="font-mono">{ago(profile.lastSeenAt, profile.now)}</span></Field>
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
    <div style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex flex-wrap items-center gap-x-2">
        {profile.stale && <span aria-hidden className="size-2 shrink-0 bg-warn" />}
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

function Identity({ profile, mark }: { profile: VRChatUserProfile; mark: React.ReactNode }) {
  return (
    <ProfileHeader
      bannerUrl={profile.bannerUrl}
      pictureUrl={profile.profilePictureUrl}
      name={profile.displayName}
      id={profile.userId}
      pronouns={profile.pronouns}
      tags={profile.tags}
      lastPlatform={profile.lastPlatform}
      rank={profile.trustRank}
      representedGroup={profile.representedGroup}
      marks={mark}
    />
  )
}

/**
 * The sticky flag as a badge, with its source, when it was set, what VRChat said last and the
 * control that changes it in a card beside it.
 *
 * The flag itself is one of the things read at a glance, so it belongs with the badges rather
 * than in a block of its own. The rest is what somebody asks for only once they have noticed the
 * badge, and that is what the card is for: the two sides can disagree and the disagreement is the
 * point (user profile sync design §4) -- a user who showed 18+ once and hides it now is still
 * 18+. Clearing it is a decision, so it asks for a reason and is recorded against the account
 * that made it.
 *
 * **Three ways in, because the card holds a control.** A pointer opens it by hovering. A tap
 * opens it, since a touch screen has no hover to give. From the keyboard, Enter or Space on the
 * badge opens it and moves the focus into the card, so Clear flag is the next thing Tab reaches
 * and Escape closes the card and gives the focus back. A hover never takes the focus, or the page
 * would move under whoever was typing somewhere else.
 */
function AgeMark({
  profile,
  canEdit,
  onChanged,
}: {
  profile: VRChatUserProfile
  canEdit: boolean
  onChanged: (next: VRChatUserProfile) => void
}) {
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<boolean | null>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  /** Which of the three opened it, which is what decides whether the card takes the focus. */
  const openedBy = useRef<'hover' | 'press'>('press')
  const closing = useRef<number | undefined>(undefined)

  const flag = profile.eighteenPlus

  const stopClosing = () => {
    window.clearTimeout(closing.current)
    closing.current = undefined
  }

  // A moment's grace, so a pointer can cross the gap between the badge and the card. Never while
  // a reason is half typed: leaving the badge is not a decision to throw the form away.
  const closeSoon = () => {
    if (editing !== null) return
    stopClosing()
    closing.current = window.setTimeout(() => setOpen(false), 150)
  }

  const openOnHover = (event: React.PointerEvent) => {
    if (event.pointerType !== 'mouse') return
    stopClosing()
    openedBy.current = 'hover'
    setOpen(true)
  }

  useEffect(() => stopClosing, [])

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
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        stopClosing()
        setOpen(next)
        if (!next) {
          setEditing(null)
          setProblem(null)
        }
      }}
    >
      <Popover.Trigger asChild>
        <button
          type="button"
          onPointerEnter={openOnHover}
          onPointerLeave={closeSoon}
          onClick={(event) => {
            // Already open because the pointer is resting on it: a click keeps the card rather
            // than shutting the thing the moderator was reaching for.
            if (open && openedBy.current === 'hover') event.preventDefault()
            openedBy.current = 'press'
          }}
          className={cn(
            'inline-flex shrink-0 items-center rounded-sm border px-1 py-0 font-medium whitespace-nowrap outline-none',
            'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
            flag.verified ? 'border-ok/30 bg-ok/10 text-ok' : 'border-border text-muted-foreground',
          )}
          style={{ fontSize: '0.6875rem', borderWidth: 'var(--hairline)' }}
        >
          {flag.verified ? '18+ verified' : 'Not seen as 18+ verified'}
        </button>
      </Popover.Trigger>

      <Popover.Portal>
        {/* Drawn at the top of the page like the dropdown's list, so the popup's own scrolling
            column cannot clip it. */}
        <Popover.Content
          side="right"
          align="start"
          sideOffset={8}
          collisionPadding={8}
          onPointerEnter={stopClosing}
          onPointerLeave={closeSoon}
          onOpenAutoFocus={(event) => {
            if (openedBy.current === 'hover') event.preventDefault()
          }}
          onCloseAutoFocus={(event) => {
            if (openedBy.current === 'hover') event.preventDefault()
          }}
          className="z-50 flex w-64 max-w-[min(20rem,var(--radix-popover-content-available-width))] flex-col gap-2 rounded-sm border bg-popover p-(--panel-pad) text-popover-foreground shadow-sm outline-none"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          {flag.verified && flag.source === 'vrchat' && flag.since && (
            <div className="text-muted-foreground">First seen on VRChat <span className="font-mono">{formatDay(flag.since)}</span>.</div>
          )}
          {flag.source === 'manual' && flag.since && (
            <div className="text-muted-foreground">
              {flag.verified ? 'Set' : 'Cleared'} by {flag.setByUsername ?? 'a moderator'} on{' '}
              <span className="font-mono">{formatDay(flag.since)}</span>.
            </div>
          )}

          <div className="text-muted-foreground">
            VRChat shows this person as{' '}
            <span className="font-mono">{profile.ageVerificationStatusLastSeen ?? 'unknown'}</span>
            {profile.lastRefreshedAt ? ` as of ${ago(profile.lastRefreshedAt, profile.now)}` : ''}.
          </div>

          {canEdit && editing === null && (
            <Button
              variant="outline"
              size="xs"
              className="self-start"
              onClick={() => setEditing(!flag.verified)}
            >
              {flag.verified ? 'Clear flag' : 'Mark 18+ verified'}
            </Button>
          )}

          {editing !== null && (
            <div className="flex flex-col gap-2">
              <label className="flex flex-col gap-1">
                <span className="text-muted-foreground">Reason</span>
                <Input
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
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
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}
