import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, ApiError, type BanReasonView } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Checkbox, Hint, Outcome, Placeholder } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Settings → Moderation → the reasons a moderator picks from when writing up a ban.
 *
 * Spec 5.8.2 is the reason this is a list and not a text box: a row of buttons costs a second and
 * gets used, and it is what the accountability checks read. So the list is the group's to shape —
 * and there is no delete on it, because a case file cites a reason by id and a reason that
 * vanished would take that case file's classification with it. Switching one off keeps it on the
 * case files that picked it and removes it from the buttons, which is the only safe version of
 * "get rid of this one".
 */
export function BanReasonsCard() {
  const [reasons, setReasons] = useState<BanReasonView[] | null>(null)
  const [canEdit, setCanEdit] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [adding, setAdding] = useState(false)
  const [label, setLabel] = useState('')
  const [description, setDescription] = useState('')
  const [needsWrittenReason, setNeedsWrittenReason] = useState(false)

  const load = useCallback(
    () =>
      api
        .banReasons()
        .then((list) => {
          setReasons(list.reasons)
          setCanEdit(list.canEdit)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to see the reason list.'
              : 'Could not load the reason list.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const run = (work: Promise<unknown>) => {
    setBusy(true)
    setProblem(null)
    work
      .then(() => load())
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save the change.'))
      .finally(() => setBusy(false))
  }

  const move = (index: number, by: -1 | 1) => {
    if (!reasons) return
    const next = [...reasons]
    const to = index + by
    if (to < 0 || to >= next.length) return
    ;[next[index], next[to]] = [next[to], next[index]]
    run(api.reorderBanReasons(next.map((r) => r.id)))
  }

  return (
    <SettingsCard
      span={12}
      title="Ban reasons"
      description="The buttons a moderator taps when writing up a ban."
      footer={
        canEdit ? (
          <>
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setAdding((open) => !open)}>
              {adding ? 'Cancel' : 'Add a reason'}
            </Button>
            <Outcome tone="problem">{problem}</Outcome>
          </>
        ) : undefined
      }
    >
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !reasons ? (
        <Hint>Loading…</Hint>
      ) : (
        <>
          <ul className="flex flex-col">
            {reasons.map((reason, index) => (
              <li
                key={reason.id}
                className={cn(
                  'flex flex-wrap items-center gap-x-2 gap-y-1 border-b py-1.5 last:border-0',
                  !reason.isActive && 'text-muted-foreground',
                )}
                style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
              >
                <span className="font-medium">{reason.label}</span>
                {reason.needsWrittenReason && (
                  <span className="text-muted-foreground">needs a written reason</span>
                )}
                {!reason.isActive && <span className="text-muted-foreground">switched off</span>}
                <span className="min-w-0 flex-1 truncate text-muted-foreground" title={reason.description}>
                  {reason.description}
                </span>

                {canEdit && (
                  <div className="flex items-center gap-1">
                    <Button
                      size="icon-xs"
                      variant="ghost"
                      disabled={busy || index === 0}
                      onClick={() => move(index, -1)}
                      aria-label={`Move ${reason.label} up`}
                      title="Move up"
                    >
                      ↑
                    </Button>
                    <Button
                      size="icon-xs"
                      variant="ghost"
                      disabled={busy || index === reasons.length - 1}
                      onClick={() => move(index, 1)}
                      aria-label={`Move ${reason.label} down`}
                      title="Move down"
                    >
                      ↓
                    </Button>
                    <Button
                      size="xs"
                      variant="ghost"
                      disabled={busy}
                      onClick={() =>
                        run(
                          api.updateBanReason(reason.id, {
                            label: reason.label,
                            description: reason.description,
                            needsWrittenReason: reason.needsWrittenReason,
                            isActive: !reason.isActive,
                          }),
                        )
                      }
                    >
                      {reason.isActive ? 'Switch off' : 'Switch on'}
                    </Button>
                  </div>
                )}
              </li>
            ))}
          </ul>

          {adding && canEdit && (
            <div className="flex flex-col gap-2 rounded-md border p-3" style={{ borderWidth: 'var(--hairline)' }}>
              <div className="grid gap-2 sm:grid-cols-[12rem_minmax(0,1fr)]">
                <Input value={label} placeholder="Doxxing" onChange={(e) => setLabel(e.target.value)} maxLength={64} />
                <Input
                  value={description}
                  placeholder="What it covers, in one line."
                  onChange={(e) => setDescription(e.target.value)}
                  maxLength={256}
                />
              </div>
              <Checkbox checked={needsWrittenReason} onChange={setNeedsWrittenReason}>
                Picking this one means the written reason cannot be left empty
              </Checkbox>
              <div>
                <Button
                  size="sm"
                  disabled={busy || label.trim().length === 0}
                  onClick={() => {
                    run(
                      api
                        .createBanReason({ label: label.trim(), description: description.trim(), needsWrittenReason })
                        .then(() => {
                          setLabel('')
                          setDescription('')
                          setNeedsWrittenReason(false)
                          setAdding(false)
                        }),
                    )
                  }}
                >
                  Add it to the end
                </Button>
              </div>
            </div>
          )}

          <Hint>
            {canEdit
              ? 'There is no delete. Case files name the reason they were given, so one that disappeared would take that classification with it — switch it off instead, and it stays on the case files that picked it.'
              : 'Changing this list needs the “Edit the reason list” permission.'}
          </Hint>
        </>
      )}
    </SettingsCard>
  )
}
