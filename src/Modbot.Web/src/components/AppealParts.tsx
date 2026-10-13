import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { api, ApiError, type CaseFileSummary } from '@/lib/api'
import {
  APPEAL_NOTE_MAX,
  appealApi,
  appealPerson,
  dayOnly,
  OUTCOME_LABEL,
  todayUtc,
  type Appeal,
  type AppealOutcome,
} from '@/lib/appeals'
import { formatDay } from '@/lib/format'
import { openCase, openSubject } from '@/lib/subject'

const OUTCOME_BADGE: Record<AppealOutcome, 'warn' | 'ok' | 'secondary'> = {
  pending: 'warn',
  upheld: 'ok',
  'turned-down': 'secondary',
}

/**
 * One appeal as a list row: who, the day it was received, how it stands, the ban it is about, and
 * the two answers for somebody who may give one.
 *
 * `showPerson` is off on a person's own pane, where the name above is already theirs.
 */
export function AppealRow({
  appeal,
  showPerson,
  onChanged,
}: {
  appeal: Appeal
  showPerson: boolean
  /** Called after an answer is saved, so the list can be read again. */
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const decide = (outcome: 'upheld' | 'turned-down') => {
    setBusy(true)
    setProblem(null)
    appealApi
      .decide(appeal.id, outcome)
      .then(onChanged)
      .catch((e: unknown) => {
        setProblem(e instanceof ApiError ? e.message : 'Could not save that.')
        // Somebody else may have answered it a moment ago; read the list again so it shows.
        if (e instanceof ApiError && e.status === 409) onChanged()
      })
      .finally(() => setBusy(false))
  }

  return (
    <li
      className="flex flex-col gap-1 border-t border-t-(length:--hairline) px-(--panel-pad) py-1.5 first:border-t-0"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <div className="flex min-h-(--row-h) flex-wrap items-center gap-x-2 gap-y-1">
        {showPerson && (
          <button
            type="button"
            title={appeal.userId}
            onClick={() => openSubject({ kind: 'person', id: appeal.userId })}
            className="rounded-sm text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
          >
            {appealPerson(appeal)}
          </button>
        )}
        <Badge variant={OUTCOME_BADGE[appeal.outcome]}>{OUTCOME_LABEL[appeal.outcome]}</Badge>
        {appeal.caseFileId && (
          <button
            type="button"
            onClick={() => openCase(appeal.caseFileId!)}
            className="rounded-sm text-link hover:underline focus-visible:outline-2 focus-visible:outline-ring"
          >
            Ban{appeal.caseFileDate ? ` ${formatDay(appeal.caseFileDate)}` : ''}
          </button>
        )}
        <span className="flex-1" />
        <span className="text-muted-foreground">
          Received <span className="font-mono">{dayOnly(appeal.receivedOn)}</span>
        </span>
      </div>

      {appeal.note && <p className="max-w-3xl break-words whitespace-pre-wrap">{appeal.note}</p>}

      <div className="flex flex-wrap items-center gap-2 text-muted-foreground">
        <span>
          Logged by {appeal.loggedByName}
          {appeal.decidedAt && appeal.decidedByName && (
            <>
              {' '}· {OUTCOME_LABEL[appeal.outcome].toLowerCase()} by {appeal.decidedByName}{' '}
              <span className="font-mono">{formatDay(appeal.decidedAt)}</span>
            </>
          )}
        </span>
        <span className="flex-1" />
        {appeal.canDecide && (
          <>
            <Button size="xs" variant="outline" disabled={busy} onClick={() => decide('upheld')}>
              Uphold
            </Button>
            <Button size="xs" variant="outline" disabled={busy} onClick={() => decide('turned-down')}>
              Turn down
            </Button>
          </>
        )}
      </div>

      {problem && <p className="text-destructive">{problem}</p>}
    </li>
  )
}

/** A case file as a choice: why, and when. */
function caseLabel(file: CaseFileSummary): string {
  const why = file.reasons.map((r) => r.label).join(', ') || 'No reason recorded'
  return `${why} · ${formatDay(file.bannedAt ?? file.createdAt)}`
}

/**
 * Writes an appeal down: who, the day it was received, the ban it is about, and a note.
 *
 * With `userId` the person is fixed (their own pane); without it the form asks for the id. The ban
 * is picked from that person's case files, which are read once an id is there.
 */
export function LogAppealForm({
  userId,
  now,
  onLogged,
  onCancel,
}: {
  userId?: string
  /** The server's clock, for the last day that can be picked. */
  now: string
  onLogged: () => void
  onCancel: () => void
}) {
  const [person, setPerson] = useState(userId ?? '')
  const [asked, setAsked] = useState(userId ?? '')
  const [received, setReceived] = useState('')
  const [caseFileId, setCaseFileId] = useState('')
  const [note, setNote] = useState('')
  const [cases, setCases] = useState<CaseFileSummary[]>([])
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    if (!asked) return

    let cancelled = false
    api
      .cases({ userId: asked, limit: 50 })
      .then((list) => {
        if (!cancelled) setCases(list.cases)
      })
      .catch(() => {
        if (!cancelled) setCases([])
      })

    return () => {
      cancelled = true
    }
  }, [asked])

  const send = () => {
    setSending(true)
    setProblem(null)

    appealApi
      .log({
        userId: person.trim(),
        receivedOn: received || null,
        caseFileId: caseFileId || null,
        note: note.trim() || null,
      })
      .then(onLogged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not log the appeal.'))
      .finally(() => setSending(false))
  }

  return (
    <div className="flex flex-col gap-2 p-(--panel-pad)">
      {userId === undefined && (
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Person</span>
          <Input
            value={person}
            className="max-w-sm font-mono"
            onChange={(e) => {
              setPerson(e.target.value)
              setCaseFileId('')
            }}
            onBlur={() => setAsked(person.trim())}
            autoFocus
          />
        </label>
      )}

      <div className="flex flex-wrap gap-2">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Received</span>
          <Input type="date" max={todayUtc(now)} value={received} onChange={(e) => setReceived(e.target.value)} />
        </label>

        {asked !== '' && asked === person.trim() && cases.length > 0 && (
          <label className="flex min-w-0 flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Ban</span>
            <Select value={caseFileId} onChange={setCaseFileId} className="max-w-full">
              <option value="">None</option>
              {cases.map((file) => (
                <option key={file.id} value={file.id}>
                  {caseLabel(file)}
                </option>
              ))}
            </Select>
          </label>
        )}
      </div>

      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Note</span>
        <Textarea className="min-h-16" value={note} maxLength={APPEAL_NOTE_MAX} onChange={(e) => setNote(e.target.value)} />
      </label>

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={send} disabled={sending || person.trim().length === 0}>
          {sending ? 'Saving…' : 'Log appeal'}
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel} disabled={sending}>
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
