import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { ago } from '@/lib/format'
import {
  failure,
  moderationApi,
  TARGETS,
  targetLabel,
  type ModerationTarget,
  type RuleKind,
  type RuleTests,
  type TestRun,
  type TestSample,
} from '@/lib/autoMod'
import { cn } from '@/lib/utils'
import { LongField, Outcome, Placeholder } from '../fields'

/** A rule's test set: the samples, adding one, and the last runs (AI moderation design §12). */
export function TestSetDialog({
  rule,
  open,
  onClose,
  onRan,
}: {
  rule: { kind: RuleKind; id: string; name: string } | null
  open: boolean
  onClose: () => void
  onRan: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      {open && rule && <TestSet key={rule.id} rule={rule} onClose={onClose} onRan={onRan} />}
    </Dialog>
  )
}

function TestSet({
  rule,
  onClose,
  onRan,
}: {
  rule: { kind: RuleKind; id: string; name: string }
  onClose: () => void
  onRan: () => void
}) {
  const [data, setData] = useState<RuleTests | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [text, setText] = useState('')
  const [shouldFlag, setShouldFlag] = useState(true)
  const [note, setNote] = useState('')
  const [target, setTarget] = useState<ModerationTarget>('discordMessage')

  const load = useCallback(
    () =>
      moderationApi
        .tests(rule.kind, rule.id)
        .then((next) => {
          setData(next)
          setProblem(null)
        })
        .catch((e: unknown) => setProblem(failure(e, 'Could not load the test set.'))),
    [rule.kind, rule.id],
  )

  useEffect(() => {
    void load()
  }, [load])

  const run = (work: () => Promise<unknown>, after?: () => void) => {
    setBusy(true)
    setProblem(null)
    work()
      .then(() => load())
      .then(() => after?.())
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the change.')))
      .finally(() => setBusy(false))
  }

  const add = () =>
    run(
      () =>
        moderationApi.addSample(rule.kind, rule.id, {
          text,
          shouldFlag,
          note: note.trim() || null,
          target,
        }),
      () => {
        setText('')
        setNote('')
      },
    )

  const latest = data?.runs[0]

  return (
    <DialogContent
      title={`Test set · ${rule.name}`}
      className="max-w-[820px]"
      bodyClassName="flex max-h-[75vh] flex-col gap-4 overflow-y-auto"
    >
      {!data ? (
        <Placeholder>{problem ?? 'Loading…'}</Placeholder>
      ) : (
        <>
          <Samples samples={data.samples} busy={busy} onDelete={(id) => run(() => moderationApi.deleteSample(rule.kind, rule.id, id))} />

          <div className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
            <LongField label="Sample text" value={text} placeholder="" rows={2} onChange={setText} />
            <div className="flex flex-wrap items-center gap-3">
              <select
                value={shouldFlag ? 'flag' : 'no'}
                onChange={(e) => setShouldFlag(e.target.value === 'flag')}
                className="h-8 rounded-md border border-input bg-transparent px-2 text-foreground"
                aria-label="Expected"
              >
                <option value="flag">Should flag</option>
                <option value="no">Should not flag</option>
              </select>
              <select
                value={target}
                onChange={(e) => setTarget(e.target.value as ModerationTarget)}
                className="h-8 rounded-md border border-input bg-transparent px-2 text-foreground"
                aria-label="Kind of text"
              >
                {TARGETS.map((t) => (
                  <option key={t.value} value={t.value}>
                    {t.label}
                  </option>
                ))}
              </select>
              <Input
                className="h-8 max-w-56"
                placeholder="Note"
                aria-label="Note"
                maxLength={500}
                value={note}
                onChange={(e) => setNote(e.target.value)}
              />
              <Button size="xs" variant="outline" disabled={busy || !text.trim()} onClick={add}>
                Add
              </Button>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button
              size="sm"
              disabled={busy || data.samples.length === 0}
              onClick={() => run(() => moderationApi.runTests(rule.kind, rule.id), onRan)}
            >
              {busy ? 'Running…' : 'Run'}
            </Button>
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Close
            </Button>
            <Outcome tone="problem">{problem}</Outcome>
          </div>

          {latest && <Run run={latest} version={data.ruleVersion} />}
          {data.runs.length > 1 && <Earlier runs={data.runs.slice(1)} />}
        </>
      )}
    </DialogContent>
  )
}

function Samples({
  samples,
  busy,
  onDelete,
}: {
  samples: TestSample[]
  busy: boolean
  onDelete: (id: string) => void
}) {
  if (samples.length === 0)
    return (
      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        No samples
      </span>
    )

  return (
    <ul className="flex flex-col" style={{ fontSize: 'var(--text-small)' }}>
      {samples.map((s) => (
        <li
          key={s.id}
          className="flex items-start gap-2 border-b py-1.5 last:border-0"
          style={{ borderBottomWidth: 'var(--hairline)' }}
        >
          <Badge variant={s.shouldFlag ? 'default' : 'outline'}>{s.shouldFlag ? 'Should flag' : 'Should not'}</Badge>
          <div className="min-w-0 flex-1">
            <div className="break-words">{s.text}</div>
            <div className="text-muted-foreground">
              {[targetLabel(s.target), s.note, s.seeded ? 'Added by Modbot' : null].filter(Boolean).join(' · ')}
            </div>
          </div>
          <Button size="xs" variant="ghost" disabled={busy} onClick={() => onDelete(s.id)}>
            Delete
          </Button>
        </li>
      ))}
    </ul>
  )
}

function Run({ run, version }: { run: TestRun; version: number }) {
  const now = new Date().toISOString()

  return (
    <div className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">
          {run.caught} of {run.shouldFlagCount} caught · {run.wronglyFlagged} of {run.shouldNotFlagCount} wrongly
          flagged
        </span>
        {run.model && <Badge variant="outline">{run.model}</Badge>}
        <span className="text-muted-foreground">
          {[ago(run.ranAt, now), run.ranBy, run.ruleVersion === version ? null : `Version ${run.ruleVersion}`]
            .filter(Boolean)
            .join(' · ')}
        </span>
      </div>
      {run.aiSkipped && <Outcome tone="problem">{run.aiSkipped}</Outcome>}

      <ul className="flex flex-col">
        {run.results.map((r) => {
          const right = r.shouldFlag === r.flagged
          return (
            <li
              key={r.sampleId}
              className="flex items-start gap-2 border-b py-1.5 last:border-0"
              style={{ borderBottomWidth: 'var(--hairline)' }}
            >
              <Badge variant={right ? 'secondary' : 'destructive'}>
                {right ? (r.flagged ? 'Caught' : 'Not flagged') : r.flagged ? 'Wrongly flagged' : 'Missed'}
              </Badge>
              <div className={cn('min-w-0 flex-1', right && 'text-muted-foreground')}>
                <div className="break-words">{r.text}</div>
                {r.matched && (
                  <div>
                    <span className="font-mono">{r.term}</span> → “{r.matched}”
                  </div>
                )}
                {r.reason && <div className="text-muted-foreground">{r.reason}</div>}
              </div>
            </li>
          )
        })}
      </ul>
    </div>
  )
}

function Earlier({ runs }: { runs: TestRun[] }) {
  const now = new Date().toISOString()

  return (
    <ul className="flex flex-col" style={{ fontSize: 'var(--text-small)' }}>
      {runs.map((r) => (
        <li
          key={r.id}
          className="flex flex-wrap items-center gap-2 border-b py-1 text-muted-foreground last:border-0"
          style={{ borderBottomWidth: 'var(--hairline)' }}
        >
          <span>{ago(r.ranAt, now)}</span>
          <span>
            {r.caught} of {r.shouldFlagCount} caught · {r.wronglyFlagged} wrongly flagged
          </span>
          {r.model && <Badge variant="outline">{r.model}</Badge>}
          <span>Version {r.ruleVersion}</span>
        </li>
      ))}
    </ul>
  )
}
