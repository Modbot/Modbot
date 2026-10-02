import { useCallback, useEffect, useRef, useState } from 'react'
import { Trash2 } from 'lucide-react'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { dateTime } from '@/components/charts/format'
import { ApiError } from '@/lib/api'
import { listRoleApi, type ListRole, type ListRolePlan, type ListRolePreview, type ListRoles } from '@/lib/listRoles'
import { ConfirmButton, Fact, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** `#discord/lists/<id>`: the Lists page's "Give a Discord role" opens this card with that list picked. */
const HASH = /^#discord\/lists\/([^/]+)$/

/** What a preview on screen was asked for, so the button it unlocks only acts on what was shown. */
type Shown = { kind: 'all' } | { kind: 'new'; listId: string; roleId: string } | { kind: 'one'; id: string }

/** Settings → Discord → Roles from lists (roles from lists design §10). */
export function ListRolesCard() {
  const [data, setData] = useState<ListRoles | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      listRoleApi
        .get()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load roles from lists.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Roles from lists">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <ListRolesForm data={data} onChanged={setData} />
}

function ListRolesForm({ data, onChanged }: { data: ListRoles; onChanged: (next: ListRoles) => void }) {
  const [on, setOn] = useState(data.on)
  const [listId, setListId] = useState(() => HASH.exec(window.location.hash)?.[1] ?? '')
  const [roleId, setRoleId] = useState('')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [preview, setPreview] = useState<ListRolePreview | null>(null)
  const [shown, setShown] = useState<Shown | null>(null)

  const top = useRef<HTMLDivElement>(null)

  // Opened from a list: bring the card into view with the list already picked.
  useEffect(() => {
    if (HASH.test(window.location.hash)) top.current?.scrollIntoView({ block: 'center' })
  }, [])

  const after = (next: ListRoles) => {
    onChanged(next)
    setOn(next.on)
  }

  const act = (work: () => Promise<ListRoles>, done?: () => void) => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    work()
      .then((next) => {
        after(next)
        setPreview(null)
        setShown(null)
        setSaved(true)
        done?.()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  const look = (what: Shown) => {
    setBusy(true)
    setSaved(false)
    setProblem(null)
    setPreview(null)
    setShown(null)

    const body = what.kind === 'all' ? {} : what.kind === 'new' ? { listId: what.listId, discordRoleId: what.roleId } : { id: what.id }

    listRoleApi
      .preview(body)
      .then((next) => {
        setPreview(next)
        setShown(what)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not work out who would change.'))
      .finally(() => setBusy(false))
  }

  const turningOn = on && !data.on
  const allShown = shown?.kind === 'all'
  const newShown = shown?.kind === 'new' && shown.listId === listId && shown.roleId === roleId
  const newPlan = newShown ? preview?.plans[0] : undefined

  const lists = [{ heading: null, options: data.lists.map((l) => ({ id: l.id, label: l.name, marks: [] })) }]

  return (
    <SettingsCard
      title="Roles from lists"
      footer={
        <>
          {turningOn && !allShown ? (
            <Button type="button" size="xs" disabled={busy || !data.canPreview} onClick={() => look({ kind: 'all' })}>
              Show who would change
            </Button>
          ) : turningOn ? (
            <>
              <Button type="button" size="xs" variant="destructive" disabled={busy} onClick={() => act(() => listRoleApi.set(true))}>
                Turn on
              </Button>
              <Button
                type="button"
                size="xs"
                variant="ghost"
                onClick={() => {
                  setOn(false)
                  setPreview(null)
                  setShown(null)
                }}
              >
                Cancel
              </Button>
            </>
          ) : (
            <Button type="button" size="xs" disabled={busy || on === data.on} onClick={() => act(() => listRoleApi.set(on))}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          )}
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div ref={top} className="grid gap-3 sm:grid-cols-2">
        <Fact label="Last checked" value={data.ranAt ? dateTime(data.ranAt) : 'Never'} />
        <Fact label="Bot in Discord" value={data.botCanManageRoles ? 'Manage roles' : 'No manage roles'} />
      </div>

      <Outcome tone="problem">{data.problem}</Outcome>

      <Switch checked={on} onChange={setOn}>
        Give roles from lists
      </Switch>

      {data.roles.length > 0 && (
        <ul className="flex flex-col border-y border-y-(length:--hairline)">
          {data.roles.map((role) => (
            <RoleRow
              key={role.id}
              role={role}
              busy={busy}
              canPreview={data.canPreview}
              canApply={data.canApply}
              plan={shown?.kind === 'one' && shown.id === role.id ? preview?.plans[0] : undefined}
              onLook={() => look({ kind: 'one', id: role.id })}
              onSwitch={(enabled) => act(() => listRoleApi.update(role.id, enabled))}
              onRemove={() => act(() => listRoleApi.remove(role.id))}
              onApply={(taking, leaving, giving) => act(() => listRoleApi.apply(role.id, taking, leaving, giving))}
            />
          ))}
        </ul>
      )}

      <div className="flex max-w-lg flex-col gap-3">
        <Picker
          label="List"
          value={listId}
          onChange={setListId}
          groups={lists}
          current={listId ? { label: data.lists.find((l) => l.id === listId)?.name ?? listId, marks: [] } : null}
          allowNone
          error={null}
        />
        <RolePicker label="Discord role" value={roleId} onChange={setRoleId} needsAssign />
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy || !listId || !roleId || !data.canPreview}
            onClick={() => look({ kind: 'new', listId, roleId })}
          >
            Show who would change
          </Button>
          <Button
            type="button"
            size="sm"
            disabled={busy || !newPlan || newPlan.problem !== null}
            onClick={() =>
              act(
                () => listRoleApi.add(listId, roleId),
                () => {
                  setListId('')
                  setRoleId('')
                },
              )
            }
          >
            Give this role
          </Button>
        </div>
      </div>

      {preview && shown?.kind !== 'one' && (
        <div className="flex flex-col gap-4">
          {preview.plans.length === 0 ? (
            <Fact label="Changes found" value="0" />
          ) : (
            preview.plans.map((plan, i) => <PlanView key={plan.id ?? i} plan={plan} />)
          )}
        </div>
      )}
    </SettingsCard>
  )
}

function RoleRow({
  role,
  busy,
  canPreview,
  canApply,
  plan,
  onLook,
  onSwitch,
  onRemove,
  onApply,
}: {
  role: ListRole
  busy: boolean
  canPreview: boolean
  canApply: boolean
  plan: ListRolePlan | undefined
  onLook: () => void
  onSwitch: (enabled: boolean) => void
  onRemove: () => void
  onApply: (taking: number, leaving: number, giving: number) => void
}) {
  // Switching one back on shows what it would do first, like adding one; Turn on then saves it.
  const [turningOn, setTurningOn] = useState(false)

  // Apply only after this list's own changes are on screen, and only for the changes shown.
  const canPressApply = canApply && role.stoppedAt !== null && plan !== undefined && plan.problem === null
  const canTurnOn = turningOn && plan !== undefined && plan.problem === null

  return (
    <li className="flex flex-col gap-2 border-b border-b-(length:--hairline) py-2 last:border-0">
      <div className="flex flex-wrap items-center gap-3">
        <span className="min-w-0 flex-1 truncate" style={{ fontSize: 'var(--text-small)' }}>
          {role.listName || role.listId} → {role.roleName ?? role.discordRoleId}
        </span>

        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Given <span className="font-mono">{role.given.toLocaleString()}</span>
        </span>

        <Switch
          checked={role.enabled || turningOn}
          disabled={busy || (!role.enabled && !canPreview)}
          onChange={(enabled) => {
            if (enabled && !role.enabled) {
              setTurningOn(true)
              onLook()
            } else if (!enabled && turningOn) {
              setTurningOn(false)
            } else {
              onSwitch(enabled)
            }
          }}
        >
          On
        </Switch>

        {canTurnOn && (
          <Button
            type="button"
            size="xs"
            variant="destructive"
            disabled={busy}
            onClick={() => {
              setTurningOn(false)
              onSwitch(true)
            }}
          >
            Turn on
          </Button>
        )}

        <Button type="button" size="xs" variant="outline" disabled={busy || !canPreview} onClick={onLook}>
          Show who would change
        </Button>

        {canPressApply && (
          <Button type="button" size="xs" variant="destructive" disabled={busy} onClick={() => onApply(plan.taking, plan.leaving, plan.giving)}>
            Apply
          </Button>
        )}

        <ConfirmButton size="sm" disabled={busy} onConfirm={onRemove} confirm="Remove">
          <Trash2 className="size-4" />
          <span className="sr-only">Remove</span>
        </ConfirmButton>
      </div>

      <Outcome tone="problem">{role.problem}</Outcome>

      {plan && <PlanView plan={plan} />}
    </li>
  )
}

/** One list's preview: the counts, and who would be given or lose the role. */
function PlanView({ plan }: { plan: ListRolePlan }) {
  const role = plan.roleName ?? plan.discordRoleId

  return (
    <div className="flex flex-col gap-2">
      <span className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        {plan.listName} → {role}
      </span>

      {plan.problem ? (
        <Outcome tone="problem">{plan.problem}</Outcome>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
            <Fact label="Give" value={plan.giving.toLocaleString()} mono />
            <Fact label="Take away" value={plan.taking.toLocaleString()} mono />
            <Fact label="Already have it" value={plan.alreadyHave.toLocaleString()} mono />
            <Fact label="Taken off by hand" value={plan.takenByHand.toLocaleString()} mono />
            <Fact label="No linked Discord" value={plan.noLinkedDiscord.toLocaleString()} mono />
            <Fact label="Not in the server" value={plan.notInServer.toLocaleString()} mono />
            <Fact label="Left the server" value={plan.leaving.toLocaleString()} mono />
            {plan.takesHeld > 0 && <Fact label="Not taken away yet" value={plan.takesHeld.toLocaleString()} mono />}
          </div>

          <Outcome tone="problem">{plan.heldBecause}</Outcome>

          {plan.lossStops && (
            <Outcome tone="problem">
              Stops: {(plan.taking + plan.leaving).toLocaleString()} people would lose {role} at once.
            </Outcome>
          )}
          {plan.giveStops && (
            <Outcome tone="problem">
              Stops: {plan.giving.toLocaleString()} people would be given {role} at once.
            </Outcome>
          )}

          {plan.changes.length > 0 && (
            <ul className="flex max-h-80 flex-col gap-1 overflow-y-auto">
              {plan.changes.map((change) => (
                <li key={`${change.what}:${change.discordUserId}`} style={{ fontSize: 'var(--text-small)' }}>
                  {change.name ?? change.discordUserId} — {change.what === 'give' ? `give ${role}` : `take away ${role}`}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  )
}
