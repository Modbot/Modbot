import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, ApiError, type BanReasonView, type ReasonUseName } from '@/lib/api'
import { cn } from '@/lib/utils'
import { EmptyRow } from '@/components/PanelGrid'
import { Checkbox, Outcome } from './fields'
import { SettingsCard } from './SettingsCard'

const USES: { use: ReasonUseName; label: string }[] = [
  { use: 'ban', label: 'Ban' },
  { use: 'kick', label: 'Kick' },
  { use: 'unban', label: 'Unban' },
  { use: 'reject', label: 'Reject' },
]

/** What a new reason is ticked for until somebody changes it: what a ban reason always served. */
const NEW_REASON_USES: ReasonUseName[] = ['ban', 'kick', 'reject']

/**
 * Settings → Moderation → the reasons a moderator picks from when writing up a ban.
 *
 * Spec 5.8.2 is the reason this is a list and not a text box: a row of buttons costs a second and
 * gets used, and it is what the accountability checks read. So the list is the group's to shape —
 * and there is no delete on it, because a case file cites a reason by id and a reason that
 * vanished would take that case file's classification with it. Switching one off keeps it on the
 * case files that picked it and removes it from the buttons, which is the only safe version of
 * "get rid of this one".
 *
 * Each reason is ticked for the actions that offer it, because one list serves bans, kicks, unbans
 * and rejected join requests, and the reasons a ban was lifted are not the reasons it was imposed
 * (M4 §9). The switch above the list makes a reason required beyond bans; it existed on the server
 * long before it had a control.
 */
export function BanReasonsCard() {
  const [reasons, setReasons] = useState<BanReasonView[] | null>(null)
  const [canEdit, setCanEdit] = useState(false)
  const [required, setRequired] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [adding, setAdding] = useState(false)
  const [label, setLabel] = useState('')
  const [description, setDescription] = useState('')
  const [needsWrittenReason, setNeedsWrittenReason] = useState(false)
  const [usedFor, setUsedFor] = useState<ReasonUseName[]>(NEW_REASON_USES)

  const load = useCallback(
    () =>
      api
        .banReasons()
        .then((list) => {
          setReasons(list.reasons)
          setCanEdit(list.canEdit)
          setRequired(list.reasonAlwaysRequired)
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

  const save = (reason: BanReasonView, change: Partial<Pick<BanReasonView, 'isActive' | 'usedFor'>>) =>
    run(
      api.updateBanReason(reason.id, {
        label: reason.label,
        description: reason.description,
        needsWrittenReason: reason.needsWrittenReason,
        isActive: change.isActive ?? reason.isActive,
        usedFor: change.usedFor ?? reason.usedFor,
      }),
    )

  return (
    <SettingsCard
      span={12}
      title="Ban reasons"
      flush
      footer={
        canEdit ? (
          <>
            <Button size="xs" variant="outline" disabled={busy} onClick={() => setAdding((open) => !open)}>
              {adding ? 'Cancel' : 'Add a reason'}
            </Button>
            <Outcome tone="problem">{problem}</Outcome>
          </>
        ) : undefined
      }
    >
      {error ? (
        <EmptyRow tone="danger">{error}</EmptyRow>
      ) : !reasons ? (
        <EmptyRow>Loading…</EmptyRow>
      ) : (
        <>
          <div className="border-b border-b-(length:--hairline) px-(--panel-pad) py-2">
            <Checkbox
              checked={required}
              disabled={!canEdit || busy}
              onChange={(on) => run(api.setReasonAlwaysRequired(on))}
            >
              Require a reason on kicks, unbans and rejected join requests
            </Checkbox>
          </div>

          <ul className="flex flex-col">
            {reasons.map((reason, index) => (
              <li
                key={reason.id}
                className={cn(
                  'flex min-h-(--row-h) flex-wrap items-center gap-x-2 gap-y-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-1',
                  !(adding && canEdit) && 'last:border-b-0',
                  !reason.isActive && 'text-muted-foreground',
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                <span className="font-medium">{reason.label}</span>
                {reason.needsWrittenReason && (
                  <span className="text-muted-foreground max-sm:order-2 max-sm:basis-full">needs a written reason</span>
                )}
                {!reason.isActive && (
                  <span className="text-muted-foreground max-sm:order-2 max-sm:basis-full">switched off</span>
                )}
                <span
                  className="min-w-0 flex-1 truncate text-muted-foreground max-sm:order-3 max-sm:basis-full max-sm:whitespace-normal"
                  title={reason.description}
                >
                  {reason.description}
                </span>

                <UsedForBoxes
                  name={reason.label}
                  value={reason.usedFor}
                  disabled={!canEdit || busy}
                  onChange={(next) => save(reason, { usedFor: next })}
                  className="max-sm:order-4 max-sm:basis-full"
                />

                {canEdit && (
                  <div className="flex items-center gap-1 max-sm:order-1 max-sm:ml-auto">
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
                      onClick={() => save(reason, { isActive: !reason.isActive })}
                    >
                      {reason.isActive ? 'Switch off' : 'Switch on'}
                    </Button>
                  </div>
                )}
              </li>
            ))}
          </ul>

          {adding && canEdit && (
            <div className="flex flex-col gap-2 p-(--panel-pad)">
              <div className="grid gap-2 sm:grid-cols-[12rem_minmax(0,1fr)]">
                <Input value={label} placeholder="Doxxing" onChange={(e) => setLabel(e.target.value)} maxLength={64} />
                <Input
                  value={description}
                  placeholder="Sharing someone's real-life details"
                  onChange={(e) => setDescription(e.target.value)}
                  maxLength={256}
                />
              </div>
              <Checkbox checked={needsWrittenReason} onChange={setNeedsWrittenReason}>
                Needs a written reason
              </Checkbox>
              <UsedForBoxes name="the new reason" value={usedFor} disabled={busy} onChange={setUsedFor} />
              <div>
                <Button
                  size="sm"
                  disabled={busy || label.trim().length === 0 || usedFor.length === 0}
                  onClick={() => {
                    run(
                      api
                        .createBanReason({ label: label.trim(), description: description.trim(), needsWrittenReason, usedFor })
                        .then(() => {
                          setLabel('')
                          setDescription('')
                          setNeedsWrittenReason(false)
                          setUsedFor(NEW_REASON_USES)
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
        </>
      )}
    </SettingsCard>
  )
}

/**
 * The four actions as tick boxes. The last ticked one cannot be unticked: a reason offered on
 * nothing is a switched-off reason by another name, and switching off already exists.
 */
function UsedForBoxes({
  name,
  value,
  disabled,
  onChange,
  className,
}: {
  name: string
  value: ReasonUseName[]
  disabled: boolean
  onChange: (next: ReasonUseName[]) => void
  className?: string
}) {
  return (
    <div role="group" aria-label={`Actions that offer ${name}`} className={cn('flex flex-wrap items-center gap-x-3 gap-y-1', className)}>
      {USES.map(({ use, label }) => {
        const on = value.includes(use)
        const last = on && value.length === 1

        return (
          <Checkbox
            key={use}
            checked={on}
            disabled={disabled || last}
            onChange={(next) =>
              onChange(next ? USES.map((u) => u.use).filter((u) => u === use || value.includes(u)) : value.filter((u) => u !== use))
            }
          >
            {label}
          </Checkbox>
        )
      })}
    </div>
  )
}
