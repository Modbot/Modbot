import { useCallback, useEffect, useState } from 'react'
import { Trash2 } from 'lucide-react'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ConfirmButton, Fact, Outcome, Switch } from '@/components/settings/fields'
import { SettingsCard } from '@/components/settings/SettingsCard'
import { dateTime } from '@/components/charts/format'
import {
  api,
  ApiError,
  type StaffRoleChange,
  type StaffRoleInput,
  type StaffRoleMapping,
  type StaffRolePreviewMapping,
  type StaffRolesPreview,
  type StaffRolesSettings,
} from '@/lib/api'

/**
 * Staff roles from Discord (design 2026-10-02): which Discord roles give which Modbot roles.
 *
 * One list, edited in two places that read and write the same endpoints: the whole list on
 * Settings → Discord, and one role's mappings on the roles page. Every change that can give or take
 * somebody's role is shown first as the list of who would change, and saved only from there.
 */

const DIRECTIONS = [
  { id: 'discord', label: 'Discord decides' },
  { id: 'both', label: 'Both ways' },
]

const directionLabel = (id: string) => DIRECTIONS.find((d) => d.id === id)?.label ?? id

/** A change waiting for its preview to be read and confirmed. */
type Pending = {
  confirm: string
  preview: StaffRolesPreview | null
  run: () => Promise<StaffRolesSettings>
}

function useStaffRoles() {
  const [data, setData] = useState<StaffRolesSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .staffRoles()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the roles from Discord.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return { data, setData, error, load }
}

const asPreview = (m: StaffRoleMapping): StaffRolePreviewMapping => ({
  id: m.id,
  discordRoleId: m.discordRoleId,
  roleId: m.roleId,
  direction: m.direction,
})

/** Settings → Discord: the whole list, the switch and the Apply button. */
export function StaffRolesCard() {
  const { data, setData, error, load } = useStaffRoles()

  if (error || !data) {
    return (
      <SettingsCard title="Modbot roles from Discord">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <Editor settings={data} onSaved={setData} />
}

/** The roles page: the Discord roles that give one Modbot role. */
export function RoleDiscordRoles({ roleId }: { roleId: string }) {
  const { data, setData, error, load } = useStaffRoles()

  if (error) return <Outcome tone="problem">{error}</Outcome>
  if (!data) return null

  return <Editor settings={data} onSaved={setData} forRoleId={roleId} onReload={load} />
}

function Editor({
  settings,
  onSaved,
  forRoleId,
  onReload,
}: {
  settings: StaffRolesSettings
  onSaved: (next: StaffRolesSettings) => void
  forRoleId?: string
  onReload?: () => Promise<void>
}) {
  const [pending, setPending] = useState<Pending | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [applied, setApplied] = useState<string | null>(null)

  const all = settings.mappings.map(asPreview)
  const shown = forRoleId ? settings.mappings.filter((m) => m.roleId === forRoleId) : settings.mappings

  /** Asks who would change, then waits for Save. */
  const ask = (confirm: string, after: StaffRolePreviewMapping[], run: () => Promise<StaffRolesSettings>) => {
    setProblem(null)
    setApplied(null)
    setBusy(true)
    setPending({ confirm, preview: null, run })

    api
      .previewStaffRoles(after)
      .then((preview) => setPending({ confirm, preview, run }))
      .catch((e: unknown) => {
        setPending(null)
        setProblem(e instanceof ApiError ? e.message : 'Could not work out who would change.')
      })
      .finally(() => setBusy(false))
  }

  const commit = () => {
    if (!pending) return
    setBusy(true)
    pending
      .run()
      .then((next) => {
        onSaved(next)
        setPending(null)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  const add = (body: StaffRoleInput) =>
    ask('Save', [...all, { ...body, id: null }], () => api.addStaffRole(body))

  const change = (mapping: StaffRoleMapping, direction: string) => {
    const body = { discordRoleId: mapping.discordRoleId, roleId: mapping.roleId, direction }
    ask(
      'Save',
      all.map((m) => (m.id === mapping.id ? { ...m, direction } : m)),
      () => api.setStaffRole(mapping.id, body),
    )
  }

  const remove = (mapping: StaffRoleMapping) => {
    setProblem(null)
    setBusy(true)
    api
      .deleteStaffRole(mapping.id)
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not remove it.'))
      .finally(() => setBusy(false))
  }

  const switchOn = (on: boolean) => {
    if (on) {
      ask('Turn on', all, () => api.setStaffRolesOn(true))
      return
    }
    setBusy(true)
    api
      .setStaffRolesOn(false)
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  const apply = () =>
    ask('Apply', all, () =>
      api.applyStaffRoles().then((result) => {
        setApplied(`Gave ${result.given}, took away ${result.taken}.`)
        if (result.problem) setProblem(result.problem)
        return result.settings
      }),
    )

  const body = (
    <>
      {!forRoleId && (
        <>
          <div className="grid gap-3 sm:grid-cols-2">
            <Fact label="Last checked" value={settings.ranAt ? dateTime(settings.ranAt) : 'Never'} mono={!!settings.ranAt} />
            <Fact label="Mappings" value={settings.mappings.length.toString()} mono />
          </div>
          <Outcome tone="problem">{settings.problem}</Outcome>
          <div className="flex max-w-lg flex-col gap-3">
            <Switch checked={settings.on} disabled={busy || pending !== null} onChange={switchOn}>
              Give Modbot roles from Discord roles
            </Switch>
          </div>
          {settings.heldAt && (
            <div>
              <Button type="button" size="xs" variant="outline" disabled={busy || pending !== null} onClick={apply}>
                Apply
              </Button>
            </div>
          )}
        </>
      )}

      {forRoleId && !settings.on && (
        <div>
          <Badge variant="outline">Off</Badge>
        </div>
      )}

      {shown.length > 0 && (
        <ul className="flex flex-col border-y border-y-(length:--hairline)">
          {shown.map((m) => (
            <MappingRow
              key={m.id}
              mapping={m}
              showRole={!forRoleId}
              busy={busy || pending !== null}
              onDirection={(direction) => change(m, direction)}
              onRemove={() => remove(m)}
            />
          ))}
        </ul>
      )}

      <AddMapping settings={settings} forRoleId={forRoleId} busy={busy || pending !== null} onAdd={add} />

      {pending && (
        <PreviewBlock
          pending={pending}
          busy={busy}
          onConfirm={commit}
          onCancel={() => {
            setPending(null)
            void onReload?.()
          }}
        />
      )}

      <Outcome tone="ok">{applied}</Outcome>
      <Outcome tone="problem">{problem}</Outcome>
    </>
  )

  if (forRoleId) {
    return (
      <div className="flex flex-col gap-3">
        <div className="font-label text-muted-foreground">Discord roles</div>
        {body}
      </div>
    )
  }

  return <SettingsCard title="Modbot roles from Discord">{body}</SettingsCard>
}

function MappingRow({
  mapping,
  showRole,
  busy,
  onDirection,
  onRemove,
}: {
  mapping: StaffRoleMapping
  showRole: boolean
  busy: boolean
  onDirection: (direction: string) => void
  onRemove: () => void
}) {
  const locked = busy || !mapping.canChange
  const why = mapping.canChange ? undefined : 'You can only change roles below your highest role.'

  return (
    <li className="flex flex-wrap items-center gap-3 border-b border-b-(length:--hairline) py-2 last:border-0">
      <span className="min-w-0 flex-1 truncate" style={{ fontSize: 'var(--text-small)' }}>
        {mapping.discordRoleName ?? mapping.discordRoleId}
        {showRole && <> → {mapping.roleName}</>}
      </span>

      {mapping.notSetUp && <Badge variant="destructive">Not set up</Badge>}

      <Picker
        label="Which way"
        value={mapping.direction}
        onChange={(direction) => direction !== mapping.direction && onDirection(direction)}
        groups={[{ heading: null, options: DIRECTIONS.map((o) => ({ ...o, marks: [] })) }]}
        current={{ label: directionLabel(mapping.direction), marks: [] }}
        allowNone={false}
        error={null}
        disabled={locked}
      />

      <ConfirmButton size="sm" disabled={locked} onConfirm={onRemove} confirm="Remove" title={why}>
        <Trash2 className="size-4" />
        <span className="sr-only">Remove</span>
      </ConfirmButton>

      <Outcome tone="problem">{mapping.problem}</Outcome>
    </li>
  )
}

function AddMapping({
  settings,
  forRoleId,
  busy,
  onAdd,
}: {
  settings: StaffRolesSettings
  forRoleId?: string
  busy: boolean
  onAdd: (body: StaffRoleInput) => void
}) {
  const [discordRoleId, setDiscordRoleId] = useState('')
  const [roleId, setRoleId] = useState(forRoleId ?? '')
  const [direction, setDirection] = useState('discord')

  const targets = settings.roles.filter((r) => r.canMap)
  const target = forRoleId ? settings.roles.find((r) => r.id === forRoleId) : null

  // On the roles page, a role you cannot map shows its mappings and nothing to add.
  if (forRoleId && !target?.canMap) return null

  return (
    <div className="flex max-w-lg flex-col gap-3">
      <RolePicker
        label="Discord role"
        value={discordRoleId}
        onChange={setDiscordRoleId}
        needsAssign={direction === 'both'}
      />
      {!forRoleId && (
        <Picker
          label="Modbot role"
          value={roleId}
          onChange={setRoleId}
          groups={[{ heading: null, options: targets.map((r) => ({ id: r.id, label: r.name, marks: [] })) }]}
          current={roleId ? { label: settings.roles.find((r) => r.id === roleId)?.name ?? roleId, marks: [] } : null}
          allowNone
          error={null}
        />
      )}
      <Picker
        label="Which way"
        value={direction}
        onChange={setDirection}
        groups={[{ heading: null, options: DIRECTIONS.map((o) => ({ ...o, marks: [] })) }]}
        current={{ label: directionLabel(direction), marks: [] }}
        allowNone={false}
        error={null}
      />
      <div>
        <Button
          type="button"
          size="sm"
          variant="outline"
          disabled={busy || !discordRoleId || !roleId}
          onClick={() => {
            onAdd({ discordRoleId, roleId, direction })
            setDiscordRoleId('')
            if (!forRoleId) setRoleId('')
          }}
        >
          Add
        </Button>
      </div>
    </div>
  )
}

function PreviewBlock({
  pending,
  busy,
  onConfirm,
  onCancel,
}: {
  pending: Pending
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const { preview } = pending

  return (
    <div className="flex flex-col gap-2">
      <div className="font-label text-muted-foreground">Who would change</div>

      {!preview ? (
        <EmptyRow className="px-0" tone="loading" />
      ) : (
        <>
          {preview.problems.map((p) => (
            <Outcome key={p} tone="problem">
              {p}
            </Outcome>
          ))}
          {preview.wouldStop && <Outcome tone="problem">Waits for Apply</Outcome>}

          {preview.changes.length === 0 ? (
            <span style={{ fontSize: 'var(--text-small)' }}>Nobody</span>
          ) : (
            <ul className="flex max-h-80 flex-col gap-1 overflow-y-auto">
              {preview.changes.map((c, i) => (
                <li key={i} style={{ fontSize: 'var(--text-small)' }} title={c.why}>
                  {line(c)}
                </li>
              ))}
            </ul>
          )}

          {preview.notes.length > 0 && (
            <>
              <div className="font-label text-muted-foreground">Left alone</div>
              <ul className="flex max-h-60 flex-col gap-1 overflow-y-auto">
                {preview.notes.map((c, i) => (
                  <li key={i} className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    {line(c)}
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}

      <div className="flex items-center gap-2">
        <Button type="button" size="xs" disabled={busy || !preview} onClick={onConfirm}>
          {pending.confirm}
        </Button>
        <Button type="button" size="xs" variant="ghost" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  )
}

function line(c: StaffRoleChange): string {
  const who = c.name ?? c.discordUserId ?? 'Somebody'
  const discordRole = c.discordRoleName ?? 'a Discord role'

  switch (c.what) {
    case 'give':
      return `${who} — gets ${c.roleName}`
    case 'take':
      return `${who} — loses ${c.roleName}${c.byHand ? ' (given by hand)' : ''}`
    case 'give-discord':
      return `${who} — gets ${discordRole} in Discord`
    case 'take-discord':
      return `${who} — loses ${discordRole} in Discord`
    case 'no-account':
      return `${who} — holds ${discordRole}, no Modbot account`
    case 'not-proven':
      return `${who} — holds ${discordRole}, Discord not connected`
    default:
      return `${who} — ${c.why}`
  }
}
