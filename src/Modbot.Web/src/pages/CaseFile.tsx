import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { EvidenceGallery } from '@/components/EvidenceGallery'
import { Markdown } from '@/components/Markdown'
import { ReasonButtons, WrittenReasonBox } from '@/components/CaseFileForm'
import { SubjectLink } from '@/components/facts'
import {
  api,
  ApiError,
  type BanReasonView,
  type CaseFileView,
  type ProfileAtBan,
} from '@/lib/api'
import { ago, formatDay } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * One case file, at `/cases/:id`.
 *
 * Three things sit on this page and each is a different kind of claim. The **written reason** is
 * the moderator's own account, rendered as Markdown with raw HTML dropped and images resolving
 * only to evidence attached here (evidence design §17). The **evidence** is what they uploaded.
 * The **profile snapshot** is Modbot's copy of the person as they were, and it is labelled with
 * the date it was taken and how old the profile already was then -- a bio from six hours before
 * the ban and a bio from the minute of it are not the same claim, and a page that showed both
 * the same way would be inventing the difference away.
 */
// Every control on this page is drawn from what the server said about this case file --
// `canEdit`, `canAttach`, `canViewEvidence` -- rather than from the signed-in account's flags.
// Editing is open to the author as well as to anyone who may ban, which is a fact about the row
// and not about the person, and a browser-side guess at it would be wrong for exactly the
// moderator whose own write-up it is.
export function CaseFile({
  caseId,
  onOpenSubject,
  onBack,
}: {
  caseId: string
  onOpenSubject: (id: string) => void
  onBack: () => void
}) {
  const [view, setView] = useState<CaseFileView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)
  const [images, setImages] = useState<ReadonlyMap<string, string>>(new Map())

  const load = useCallback(
    () =>
      api
        .caseFile(caseId)
        .then((next) => {
          setView(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to read case files.'
              : e instanceof ApiError && e.status === 404
                ? 'There is no case file with that link.'
                : 'Could not load this case file.',
          ),
        ),
    [caseId],
  )

  useEffect(() => {
    void load()
  }, [load])

  const noteImage = useCallback(
    (hash: string, url: string) => setImages((previous) => new Map(previous).set(hash, url)),
    [],
  )

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">
          <p>{error}</p>
          <Button variant="outline" size="sm" className="mt-3" onClick={onBack}>
            Back to bans
          </Button>
        </CardContent>
      </Card>
    )
  }

  if (!view) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
      </Card>
    )
  }

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-4">
      <Header view={view} onOpenSubject={onOpenSubject} onBack={onBack} />

      {view.withdrawn && (
        <div
          className="rounded-xl border border-warn/40 bg-warn/10 px-4 py-3"
          style={{ borderWidth: 'var(--hairline)' }}
        >
          <div className="font-medium">This case file has been withdrawn.</div>
          <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {view.withdrawnByUsername ?? 'Somebody'} withdrew it
            {view.withdrawnAt ? ` on ${formatDay(view.withdrawnAt)}` : ''}: {view.withdrawnNote}
          </p>
        </div>
      )}

      <Section title="Why they were banned">
        {editing ? (
          <EditReport view={view} onDone={() => { setEditing(false); void load() }} onCancel={() => setEditing(false)} />
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-1.5">
              {view.reasons.map((reason) => (
                <Badge key={reason.id} variant={reason.isActive ? 'default' : 'secondary'} title={reason.isActive ? undefined : 'Switched off'}>
                  {reason.label}
                </Badge>
              ))}
              <span className="flex-1" />
              {view.canEdit && (
                <Button variant="outline" size="xs" onClick={() => setEditing(true)}>
                  Edit
                </Button>
              )}
            </div>

            {view.writtenReason ? (
              <Markdown text={view.writtenReason} images={images} className="mt-2" />
            ) : (
              <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                No written reason was given.
              </p>
            )}

            <p className="mt-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Written by {view.authorUsername} on {formatDay(view.createdAt)}
              {view.updatedByUsername && view.updatedAt !== view.createdAt && (
                <> · last edited by {view.updatedByUsername} {ago(view.updatedAt, view.now)}</>
              )}
              .
            </p>
          </>
        )}
      </Section>

      <Section title="Evidence">
        {view.canViewEvidence && view.evidence ? (
          <EvidenceGallery
            caseId={view.id}
            items={view.evidence}
            delivery={view.evidenceDelivery}
            canAttach={view.canAttach}
            onChanged={() => void load()}
            onImageReady={noteImage}
          />
        ) : (
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            You do not have permission to view evidence.
          </p>
        )}
      </Section>

      <Snapshot view={view} onCaptured={setView} />

      {view.canEdit && !view.withdrawn && <Withdraw view={view} onWithdrawn={setView} />}
    </div>
  )
}

function Header({
  view,
  onOpenSubject,
  onBack,
}: {
  view: CaseFileView
  onOpenSubject: (id: string) => void
  onBack: () => void
}) {
  return (
    <div className="flex flex-wrap items-start gap-3">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-semibold" style={{ fontSize: 'calc(var(--text-base) + 2px)' }}>
            <SubjectLink id={view.userId} name={view.displayName} onOpen={onOpenSubject} />
          </span>
          {view.displayName && (
            <span className="font-mono text-muted-foreground/70" style={{ fontSize: 'var(--text-small)' }}>
              {view.userId}
            </span>
          )}
        </div>
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {view.bannedAt ? <>Banned {formatDay(view.bannedAt)}</> : <>No ban time recorded</>}
          {view.bannedBy && (
            <>
              {' '}by{' '}
              <SubjectLink id={view.bannedBy.id} name={view.bannedBy.name} onOpen={onOpenSubject} />
            </>
          )}
          {view.auditEntryId && <> · audit entry {view.auditEntryId}</>}
        </p>
      </div>
      <Button variant="outline" size="sm" onClick={onBack}>
        Back to bans
      </Button>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="flex flex-col gap-2 px-5">
        <div className="font-medium">{title}</div>
        {children}
      </CardContent>
    </Card>
  )
}

function EditReport({
  view,
  onDone,
  onCancel,
}: {
  view: CaseFileView
  onDone: () => void
  onCancel: () => void
}) {
  const [reasons, setReasons] = useState<BanReasonView[]>([])
  const [picked, setPicked] = useState<string[]>(view.reasons.map((r) => r.id))
  const [text, setText] = useState(view.writtenReason)
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    api
      .banReasons()
      .then((list) => setReasons(list.reasons.filter((r) => r.isActive)))
      .catch(() => setProblem('Could not load the reason list.'))
  }, [])

  const save = () => {
    setSaving(true)
    setProblem(null)

    api
      .updateCaseFile(view.id, { reasonIds: picked, writtenReason: text })
      .then(() => onDone())
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save the change.'))
      .finally(() => setSaving(false))
  }

  return (
    <div className="flex flex-col gap-3">
      <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
      <WrittenReasonBox reasons={reasons} picked={picked} value={text} onChange={setText} />

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={save} disabled={saving || picked.length === 0}>
          {saving ? 'Saving…' : 'Save the case file'}
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        {problem && (
          <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {problem}
          </span>
        )}
      </div>
    </div>
  )
}

/**
 * The person as they were, with the date on it.
 *
 * The banned person edits their name and clears their bio within the hour, which is exactly why
 * this is worth keeping — and exactly why it must never be read as current. So the date leads,
 * the age of the profile at capture follows it, and the one permitted recapture is offered only
 * when VRChat has actually answered with something newer.
 */
function Snapshot({ view, onCaptured }: { view: CaseFileView; onCaptured: (next: CaseFileView) => void }) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const snapshot = view.snapshot
  const profile = snapshot.profile

  const captureAgain = () => {
    setBusy(true)
    setProblem(null)

    api
      .captureCaseFileAgain(view.id)
      .then(onCaptured)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not capture the profile again.'))
      .finally(() => setBusy(false))
  }

  return (
    <Section title="The profile at the time">
      <div
        className="rounded-md bg-muted/40 px-3 py-2 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {snapshot.explanation}
      </div>

      {snapshot.canCaptureAgain && (
        <div className="flex flex-wrap items-center gap-2">
          <Button size="xs" variant="outline" onClick={captureAgain} disabled={busy}>
            {busy ? 'Capturing…' : 'Refresh and capture again'}
          </Button>
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Once only.
          </span>
        </div>
      )}

      {problem && (
        <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {problem}
        </p>
      )}

      {profile ? <ProfileBlock profile={profile} /> : null}

      {snapshot.membership && (
        <div className="rounded-md border px-3 py-2" style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}>
          <div className="font-medium">Membership at the time</div>
          <p className="mt-1 text-muted-foreground">
            {snapshot.membership.isMember ? 'A member' : 'No longer a member'}
            {snapshot.membership.joinedAt ? `, joined ${formatDay(snapshot.membership.joinedAt)}` : ''}
            {snapshot.membership.membershipStatus ? ` · ${snapshot.membership.membershipStatus}` : ''}.
          </p>
          {snapshot.membership.roleIds.length > 0 && (
            <div className="mt-1 flex flex-wrap gap-1">
              {snapshot.membership.roleIds.map((id) => (
                <span
                  key={id}
                  className="rounded-full border px-2 py-0.5 font-mono text-muted-foreground"
                  style={{ borderWidth: 'var(--hairline)', fontSize: '0.6875rem' }}
                >
                  {id}
                </span>
              ))}
            </div>
          )}
          {snapshot.membership.managerNotes && (
            <p className="mt-1 whitespace-pre-wrap break-words">{snapshot.membership.managerNotes}</p>
          )}
        </div>
      )}

      {snapshot.banListEntry && (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          On the group's ban list
          {snapshot.banListEntry.bannedAt ? ` since ${formatDay(snapshot.banListEntry.bannedAt)}` : ''}
          {snapshot.banListEntry.liftedAt ? `; lifted by ${formatDay(snapshot.banListEntry.liftedAt)}` : ''}.
        </p>
      )}
    </Section>
  )
}

function ProfileBlock({ profile }: { profile: ProfileAtBan }) {
  const picture = profile.profilePictureUrl || profile.avatarThumbnailUrl

  return (
    <div className="flex gap-3">
      {/* The image is fetched from VRChat's host as it was then, and may already be gone. That is
          the honest state: Modbot never copied the bytes. */}
      {picture ? (
        <img
          src={picture}
          alt=""
          className="size-16 shrink-0 rounded-md bg-muted object-cover"
          referrerPolicy="no-referrer"
        />
      ) : (
        <div className="size-16 shrink-0 rounded-md bg-muted" />
      )}

      <div className="min-w-0 flex-1" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium" style={{ fontSize: 'var(--text-base)' }}>
            {profile.displayName ?? <span className="font-mono">{profile.userId}</span>}
          </span>
          {profile.pronouns && <span className="text-muted-foreground">{profile.pronouns}</span>}
          {profile.eighteenPlus.verified && (
            <span
              className="inline-flex items-center rounded-full border border-transparent bg-ok/15 px-2 py-0.5 font-medium text-ok"
              style={{ borderWidth: 'var(--hairline)' }}
            >
              18+ verified
            </span>
          )}
        </div>

        {profile.statusDescription && (
          <div className="mt-0.5 text-muted-foreground">“{profile.statusDescription}”</div>
        )}

        {/* User-authored text, rendered as text. Never as HTML, and never as Markdown either --
            it is a bio, not a document. */}
        {profile.bio && <p className="mt-2 whitespace-pre-wrap break-words">{profile.bio}</p>}

        <dl className="mt-2 flex flex-wrap gap-x-4 gap-y-0.5 text-muted-foreground">
          {profile.dateJoined && (
            <div className="flex gap-1">
              <dt>Joined VRChat:</dt>
              <dd className="text-foreground">{formatDay(profile.dateJoined)}</dd>
            </div>
          )}
          {profile.lastPlatform && (
            <div className="flex gap-1">
              <dt>Last platform:</dt>
              <dd className="text-foreground">{profile.lastPlatform}</dd>
            </div>
          )}
          {profile.ageVerificationStatus && (
            <div className="flex gap-1">
              <dt>VRChat showed:</dt>
              <dd className="font-mono text-foreground">{profile.ageVerificationStatus}</dd>
            </div>
          )}
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

function Withdraw({ view, onWithdrawn }: { view: CaseFileView; onWithdrawn: (next: CaseFileView) => void }) {
  const [open, setOpen] = useState(false)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const submit = () => {
    setBusy(true)
    setProblem(null)

    api
      .withdrawCaseFile(view.id, note.trim())
      .then((next) => {
        onWithdrawn(next)
        setOpen(false)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not withdraw it.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className={cn('flex flex-col gap-2', !open && 'items-start')}>
      {!open ? (
        <Button variant="ghost" size="xs" className="text-muted-foreground" onClick={() => setOpen(true)}>
          Withdraw this case file
        </Button>
      ) : (
        <div
          className="rounded-md border px-3 py-2"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
        >
          <div className="font-medium">Why are you withdrawing it?</div>
          <p className="mt-1 text-muted-foreground">
            It can no longer be edited after this.
          </p>
          <textarea
            className="mt-2 w-full rounded-md border bg-background px-2 py-1"
            style={{ borderWidth: 'var(--hairline)' }}
            rows={3}
            value={note}
            maxLength={2000}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Wrong person; the display name matched somebody else."
          />
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <Button size="xs" variant="destructive" onClick={submit} disabled={busy || note.trim().length === 0}>
              {busy ? 'Withdrawing…' : 'Withdraw'}
            </Button>
            <Button size="xs" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            {problem && <span className="text-destructive">{problem}</span>}
          </div>
        </div>
      )}
    </div>
  )
}
