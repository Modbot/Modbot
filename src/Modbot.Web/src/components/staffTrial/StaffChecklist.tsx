import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { Select } from '@/components/ui/select'
import { Outcome } from '@/components/settings/fields'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { ApiError, api, type StaffTrial, type StaffTrialItem, type UserSummary } from '@/lib/api'
import { formatDay } from '@/lib/format'

/** The most a written answer or the buddy's note can hold; the server refuses more. */
const LONGEST = 4000

/** How many of a kind are done: answered for a task, signed off for a line. */
function progress(trial: StaffTrial): { tasks: [number, number]; signOffs: [number, number] } {
  const tasks = trial.items.filter((i) => i.kind === 'task')
  const signOffs = trial.items.filter((i) => i.kind === 'sign-off')

  return {
    tasks: [tasks.filter((i) => i.answer !== null).length, tasks.length],
    signOffs: [signOffs.filter((i) => i.signedOffAt !== null).length, signOffs.length],
  }
}

/** "2 of 5 tasks, 1 of 3 signed off": where a trial stands, in one line. */
export function ProgressLine({ trial }: { trial: StaffTrial }) {
  const { tasks, signOffs } = progress(trial)

  return (
    <span>
      {tasks[0]} of {tasks[1]} tasks · {signOffs[0]} of {signOffs[1]} signed off
    </span>
  )
}

/**
 * One new staff member's checklist: the tasks they write answers to, the lines their buddy signs
 * off, and the buddy's note. What each person can do is decided by the server (`canAnswer`,
 * `canSignOff`, `canManage`); this draws the controls those flags allow and nothing else.
 *
 * `onChange` gets the trial the server answered with after every save.
 */
export function StaffChecklist({ trial, onChange, onRemoved }: {
  trial: StaffTrial
  onChange: (next: StaffTrial) => void
  onRemoved?: () => void
}) {
  const tasks = trial.items.filter((i) => i.kind === 'task')
  const signOffs = trial.items.filter((i) => i.kind === 'sign-off')

  return (
    <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex flex-wrap gap-x-6 gap-y-1">
        <span>
          <span className="text-muted-foreground">Started </span>
          <span className="font-mono">{formatDay(trial.startedAt)}</span>
        </span>
        <span>
          <span className="text-muted-foreground">Buddy </span>
          {trial.buddyName}
        </span>
        <span className="text-muted-foreground">
          <ProgressLine trial={trial} />
        </span>
        {trial.closedAt && (
          <span>
            <span className="text-muted-foreground">Closed </span>
            <span className="font-mono">{formatDay(trial.closedAt)}</span>
          </span>
        )}
      </div>

      {tasks.length > 0 && (
        <section className="flex flex-col gap-2">
          <h4 className="font-medium">Tasks</h4>
          {tasks.map((item) => (
            <TaskRow key={item.id} trial={trial} item={item} onChange={onChange} />
          ))}
        </section>
      )}

      {signOffs.length > 0 && (
        <section className="flex flex-col gap-2">
          <h4 className="font-medium">Buddy sign-off</h4>
          {signOffs.map((item) => (
            <SignOffRow key={item.id} trial={trial} item={item} onChange={onChange} />
          ))}
        </section>
      )}

      <BuddyNote trial={trial} onChange={onChange} />

      {trial.canManage && <Manage trial={trial} onChange={onChange} onRemoved={onRemoved} />}
    </div>
  )
}

function TaskRow({ trial, item, onChange }: {
  trial: StaffTrial
  item: StaffTrialItem
  onChange: (next: StaffTrial) => void
}) {
  const [text, setText] = useState(item.answer ?? '')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // Another save of this trial (a sign-off, the buddy's note) hands back the same answer; only a
  // different one from the server replaces what is typed.
  const [seen, setSeen] = useState(item.answer)
  if (seen !== item.answer) {
    setSeen(item.answer)
    setText(item.answer ?? '')
  }

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .answerStaffTrialTask(trial.accountId, item.id, text)
      .then((next) => {
        onChange(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex flex-col gap-1 rounded-sm border border-(length:--hairline) p-2">
      <span className="font-medium">{item.text}</span>
      {trial.canAnswer ? (
        <>
          <Textarea
            rows={3}
            maxLength={LONGEST}
            value={text}
            aria-label={item.text}
            onChange={(e) => {
              setText(e.target.value)
              setSaved(false)
            }}
          />
          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" size="xs" onClick={save} disabled={busy || text.trim() === (item.answer ?? '')}>
              {busy ? 'Saving…' : 'Save answer'}
            </Button>
            <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
            <Outcome tone="problem">{problem}</Outcome>
          </div>
        </>
      ) : item.answer ? (
        <p className="whitespace-pre-wrap">{item.answer}</p>
      ) : (
        <p className="text-muted-foreground">No answer yet.</p>
      )}
    </div>
  )
}

function SignOffRow({ trial, item, onChange }: {
  trial: StaffTrial
  item: StaffTrialItem
  onChange: (next: StaffTrial) => void
}) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const signed = item.signedOffAt !== null

  const press = () => {
    setBusy(true)
    setProblem(null)

    api
      .signOffStaffTrialLine(trial.accountId, item.id, !signed)
      .then(onChange)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex flex-wrap items-center justify-between gap-2 rounded-sm border border-(length:--hairline) p-2">
      <div className="flex min-w-0 flex-col">
        <span className="font-medium">{item.text}</span>
        <span className="text-muted-foreground">
          {signed
            ? `Signed off${item.signedOffBy ? ` by ${item.signedOffBy}` : ''} on ${formatDay(item.signedOffAt!)}`
            : 'Not signed off'}
        </span>
      </div>
      <div className="flex items-center gap-2">
        <Outcome tone="problem">{problem}</Outcome>
        {trial.canSignOff && (
          <Button type="button" size="xs" variant={signed ? 'outline' : 'default'} onClick={press} disabled={busy}>
            {signed ? 'Take back' : 'Sign off'}
          </Button>
        )}
      </div>
    </div>
  )
}

function BuddyNote({ trial, onChange }: { trial: StaffTrial; onChange: (next: StaffTrial) => void }) {
  const [text, setText] = useState(trial.buddyNote ?? '')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const [seen, setSeen] = useState(trial.buddyNote)
  if (seen !== trial.buddyNote) {
    setSeen(trial.buddyNote)
    setText(trial.buddyNote ?? '')
  }

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setStaffTrialNote(trial.accountId, text)
      .then((next) => {
        onChange(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  if (!trial.canSignOff) {
    return trial.buddyNote ? (
      <section className="flex flex-col gap-1">
        <h4 className="font-medium">Buddy note</h4>
        <p className="whitespace-pre-wrap">{trial.buddyNote}</p>
      </section>
    ) : null
  }

  return (
    <section className="flex flex-col gap-1">
      <h4 className="font-medium">Buddy note</h4>
      <Textarea
        rows={3}
        maxLength={LONGEST}
        value={text}
        aria-label="Buddy note"
        onChange={(e) => {
          setText(e.target.value)
          setSaved(false)
        }}
      />
      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" size="xs" onClick={save} disabled={busy || text.trim() === (trial.buddyNote ?? '')}>
          {busy ? 'Saving…' : 'Save note'}
        </Button>
        <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </section>
  )
}

/** The buddy picker, for the accounts that are turned on and are not the one on trial. */
export function BuddyPicker({ accountId, value, onChange }: {
  accountId: string
  value: string
  onChange: (id: string) => void
}) {
  const [accounts, setAccounts] = useState<UserSummary[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    let live = true

    api
      .users()
      .then((all) => live && setAccounts(all))
      .catch((e: unknown) => live && setProblem(e instanceof ApiError ? e.message : 'Could not read the accounts.'))

    return () => {
      live = false
    }
  }, [])

  const choices = (accounts ?? []).filter((a) => a.id !== accountId && !a.isDisabled && !a.isDeleted)

  return (
    <div className="flex flex-col gap-1">
      <Select value={value} onChange={onChange} aria-label="Buddy" disabled={!accounts}>
        <option value="">Pick a buddy</option>
        {choices.map((a) => (
          <option key={a.id} value={a.id}>
            {a.username}
          </option>
        ))}
      </Select>
      <Outcome tone="problem">{problem}</Outcome>
    </div>
  )
}

function Manage({ trial, onChange, onRemoved }: {
  trial: StaffTrial
  onChange: (next: StaffTrial) => void
  onRemoved?: () => void
}) {
  const [buddy, setBuddy] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [removing, setRemoving] = useState(false)
  const [closing, setClosing] = useState(false)

  const change = () => {
    setBusy(true)
    setProblem(null)

    api
      .setStaffTrialBuddy(trial.accountId, buddy)
      .then((next) => {
        onChange(next)
        setBuddy('')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not change the buddy.'))
      .finally(() => setBusy(false))
  }

  return (
    <section className="flex flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="min-w-40">
          <BuddyPicker accountId={trial.accountId} value={buddy} onChange={setBuddy} />
        </div>
        <Button type="button" size="xs" variant="outline" onClick={change} disabled={busy || buddy === ''}>
          Change buddy
        </Button>
        {!trial.closedAt && (
          <Button type="button" size="xs" variant="outline" onClick={() => setClosing(true)}>
            Close
          </Button>
        )}
        <Button type="button" size="xs" variant="outline" onClick={() => setRemoving(true)}>
          Remove checklist
        </Button>
        <Outcome tone="problem">{problem}</Outcome>
      </div>

      <ConfirmDialog
        open={closing}
        onOpenChange={setClosing}
        title={`Close ${trial.username}'s checklist?`}
        action="Close"
        failed="Could not close the checklist."
        onConfirm={() => api.closeStaffTrial(trial.accountId).then(onChange)}
      />

      <ConfirmDialog
        open={removing}
        onOpenChange={setRemoving}
        title={`Remove ${trial.username}'s checklist?`}
        action="Remove"
        failed="Could not remove the checklist."
        onConfirm={() => api.removeStaffTrial(trial.accountId)}
        onDone={() => onRemoved?.()}
      />
    </section>
  )
}
