import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type BanReasonView } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * The pieces of writing up a ban, shared by the form that creates a case file and the edit mode
 * on the case file page.
 *
 * Spec 5.8.2: the classification is one tap. A row of buttons costs a second and gets used; a
 * text box gets a single-digit completion rate, and a required field that is always answered the
 * same way carries no information. The written reason sits under the buttons as the *additional*
 * input it is — except where a picked reason says otherwise, which is what "Other" is for.
 */
export function ReasonButtons({
  reasons,
  picked,
  onChange,
}: {
  reasons: BanReasonView[]
  picked: string[]
  onChange: (next: string[]) => void
}) {
  const toggle = (id: string) =>
    onChange(picked.includes(id) ? picked.filter((p) => p !== id) : [...picked, id])

  if (reasons.length === 0) {
    return (
      <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        No reasons on the list.
      </p>
    )
  }

  return (
    <div role="group" aria-label="Reasons" className="flex flex-wrap gap-1.5">
      {reasons.map((reason) => (
        <button
          key={reason.id}
          type="button"
          aria-pressed={picked.includes(reason.id)}
          title={reason.description}
          onClick={() => toggle(reason.id)}
          className={cn(
            'inline-flex items-center rounded-full border px-2.5 font-medium transition-colors',
            picked.includes(reason.id)
              ? 'border-transparent bg-accent text-accent-foreground'
              : 'text-muted-foreground hover:text-foreground',
          )}
          style={{
            fontSize: 'var(--text-small)',
            borderWidth: 'var(--hairline)',
            height: 'calc(var(--control-h) - 6px)',
          }}
        >
          {reason.label}
        </button>
      ))}
    </div>
  )
}

/** The written reason, with a note saying whether it is required by what has been picked. */
export function WrittenReasonBox({
  reasons,
  picked,
  value,
  onChange,
  rows = 8,
}: {
  reasons: BanReasonView[]
  picked: string[]
  value: string
  onChange: (next: string) => void
  rows?: number
}) {
  const requiredBy = reasons.filter((r) => picked.includes(r.id) && r.needsWrittenReason)

  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">
        What happened
        {requiredBy.length > 0 ? ' (required)' : ' (optional)'}
      </span>
      <textarea
        className="w-full rounded-md border bg-background px-2 py-1 font-mono"
        style={{ borderWidth: 'var(--hairline)' }}
        rows={rows}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Followed two members between instances shouting slurs."
      />
    </label>
  )
}

/**
 * Writing up a ban that has none: the reason buttons, the written reason, and a save that lands
 * on the new case file.
 *
 * The profile snapshot is taken by the server the moment this saves, from what Modbot already
 * holds, and a fresher profile is asked for straight afterwards — which is why the page that
 * opens says whether one is on the way.
 */
export function WriteCaseFile({
  ban,
  onWritten,
  onCancel,
}: {
  /**
   * Who was banned, as much as the list that launched this knows. `bannedAt` is null on the
   * group's own ban list when VRChat did not state a time, and `auditEntryId` is null whenever
   * the ban predates Modbot's audit-log window -- the server then finds the ban itself.
   */
  ban: { userId: string; displayName: string | null; bannedAt: string | null; auditEntryId: string | null }
  onWritten: (caseId: string) => void
  onCancel: () => void
}) {
  const [reasons, setReasons] = useState<BanReasonView[] | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [text, setText] = useState('')
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [existing, setExisting] = useState<string | null>(null)

  useEffect(() => {
    api
      .banReasons()
      .then((list) => setReasons(list.reasons.filter((r) => r.isActive)))
      .catch(() => setProblem('Could not load the reason list.'))
  }, [])

  const save = () => {
    setSaving(true)
    setProblem(null)
    setExisting(null)

    api
      .createCaseFile({
        userId: ban.userId,
        auditEntryId: ban.auditEntryId,
        reasonIds: picked,
        writtenReason: text,
      })
      .then((created) => onWritten(created.case.id))
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 409) {
          const detail = e.detail as { caseId?: string } | null
          if (detail?.caseId) setExisting(detail.caseId)
        }
        setProblem(e instanceof ApiError ? e.message : 'Could not write the case file.')
      })
      .finally(() => setSaving(false))
  }

  return (
    <div className="flex flex-col gap-3">
      <div style={{ fontSize: 'var(--text-small)' }}>
        <span className="font-medium">{ban.displayName ?? ban.userId}</span>
        <span className="text-muted-foreground">
          {ban.bannedAt ? ` · banned ${formatDay(ban.bannedAt)}` : ''}
          {ban.displayName ? ` · ${ban.userId}` : ''}
        </span>
      </div>

      {reasons === null ? (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Loading the reasons…
        </p>
      ) : (
        <>
          <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
          <WrittenReasonBox reasons={reasons} picked={picked} value={text} onChange={setText} />
        </>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={save} disabled={saving || picked.length === 0}>
          {saving ? 'Writing…' : 'Write the case file'}
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        {problem && (
          <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {problem}
          </span>
        )}
        {existing && (
          <Button variant="outline" size="xs" onClick={() => onWritten(existing)}>
            Open the one that exists
          </Button>
        )}
      </div>
    </div>
  )
}
