import { useCallback, useEffect, useState } from 'react'
import { Trash2 } from 'lucide-react'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordSyncSettings, type PlannedChange, type RolePair, type SyncPreview } from '@/lib/api'
import { Fact, Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

const DECIDES = [
  { id: 'vrchat', label: 'VRChat' },
  { id: 'discord', label: 'Discord' },
  { id: 'nobody', label: 'Nobody' },
]

const BAN_ACTIONS = [
  { id: 'ban', label: 'Ban them in Discord' },
  { id: 'remove', label: 'Remove them from the server' },
]

/** Settings → Discord → Role and ban sync (M5 §3, §4). */
export function SyncCard() {
  const [data, setData] = useState<DiscordSyncSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordSync()
        .then(setData)
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the sync settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Role and ban sync">
        <Placeholder>{error ?? 'Loading…'}</Placeholder>
      </SettingsCard>
    )
  }

  return <SyncForm settings={data} onSaved={setData} />
}

function SyncForm({
  settings,
  onSaved,
}: {
  settings: DiscordSyncSettings
  onSaved: (next: DiscordSyncSettings) => void
}) {
  const [roleSyncOn, setRoleSyncOn] = useState(settings.roleSyncOn)
  const [toDiscord, setToDiscord] = useState(settings.banSyncToDiscord)
  const [toVRChat, setToVRChat] = useState(settings.banSyncToVRChat)
  const [banCopyAction, setBanCopyAction] = useState(settings.banCopyAction)

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [preview, setPreview] = useState<SyncPreview | null>(null)

  const after = (next: DiscordSyncSettings) => {
    onSaved(next)
    setRoleSyncOn(next.roleSyncOn)
    setToDiscord(next.banSyncToDiscord)
    setToVRChat(next.banSyncToVRChat)
    setBanCopyAction(next.banCopyAction)
  }

  const run = (work: () => Promise<DiscordSyncSettings>, showSaved = true) => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    work()
      .then((next) => {
        after(next)
        if (showSaved) setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  const look = (apply: boolean) => {
    setBusy(true)
    setSaved(false)
    setProblem(null)
    setPreview(null)

    ;(apply ? api.runDiscordSync() : api.previewDiscordSync())
      .then(setPreview)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not work out what would change.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Role and ban sync"
      footer={
        <>
          <Button
            type="button"
            size="sm"
            disabled={busy}
            onClick={() =>
              run(() => api.setDiscordSync({ roleSyncOn, banSyncToDiscord: toDiscord, banSyncToVRChat: toVRChat, banCopyAction }))
            }
          >
            {busy ? 'Saving…' : 'Save sync'}
          </Button>
          <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => look(false)}>
            Show what would change
          </Button>
          <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => look(true)}>
            Copy what is different
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid gap-3 sm:grid-cols-3">
        <Fact label="Roles last checked" value={when(settings.rolesRanAt)} />
        <Fact label="Bans last checked" value={when(settings.bansReadAt)} />
        <Fact label="Bot in Discord" value={permissions(settings)} />
      </div>

      <Outcome tone="problem">{settings.rolesProblem}</Outcome>
      <Outcome tone="problem">{settings.bansProblem}</Outcome>

      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked={roleSyncOn} onChange={setRoleSyncOn}>
          Keep paired roles in step
        </Switch>
        <Switch checked={toDiscord} onChange={setToDiscord}>
          Copy group bans into Discord
        </Switch>
        {toDiscord && (
          <Picker
            label="A group ban does this in Discord"
            value={banCopyAction}
            onChange={setBanCopyAction}
            groups={[{ heading: null, options: BAN_ACTIONS.map((o) => ({ ...o, marks: [] })) }]}
            current={{ label: BAN_ACTIONS.find((o) => o.id === banCopyAction)?.label ?? banCopyAction, marks: [] }}
            allowNone={false}
            error={null}
          />
        )}
        <Switch checked={toVRChat} onChange={setToVRChat}>
          Copy Discord bans into the group
        </Switch>
      </div>

      <Pairs settings={settings} busy={busy} onChanged={after} onProblem={setProblem} />

      {preview && <Preview preview={preview} />}
    </SettingsCard>
  )
}

function Pairs({
  settings,
  busy,
  onChanged,
  onProblem,
}: {
  settings: DiscordSyncSettings
  busy: boolean
  onChanged: (next: DiscordSyncSettings) => void
  onProblem: (problem: string | null) => void
}) {
  const [vrchatRoleId, setVRChatRoleId] = useState('')
  const [discordRoleId, setDiscordRoleId] = useState('')
  const [decides, setDecides] = useState('nobody')

  const act = (work: () => Promise<DiscordSyncSettings>) => {
    onProblem(null)
    work()
      .then(onChanged)
      .catch((e: unknown) => onProblem(e instanceof ApiError ? e.message : 'Could not change the pair.'))
  }

  const groupRoles = [
    { heading: null, options: settings.groupRoles.map((r) => ({ id: r.id, label: r.name, keywords: r.id, marks: [] })) },
  ]

  return (
    <div className="flex flex-col gap-3">
      <h4 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        Paired roles
      </h4>

      {settings.pairs.length > 0 && (
        <ul className="flex flex-col gap-2">
          {settings.pairs.map((pair) => (
            <PairRow
              key={pair.id}
              pair={pair}
              busy={busy}
              onSave={(body) => act(() => api.setRolePair(pair.id, body))}
              onDelete={() => act(() => api.deleteRolePair(pair.id))}
            />
          ))}
        </ul>
      )}

      <div className="flex max-w-lg flex-col gap-3">
        <Picker
          label="Group role"
          value={vrchatRoleId}
          onChange={setVRChatRoleId}
          groups={groupRoles}
          current={
            vrchatRoleId
              ? { label: settings.groupRoles.find((r) => r.id === vrchatRoleId)?.name ?? vrchatRoleId, marks: [] }
              : null
          }
          allowNone
          error={null}
        />
        <RolePicker label="Discord role" value={discordRoleId} onChange={setDiscordRoleId} needsAssign />
        <Picker
          label="Who decides"
          value={decides}
          onChange={setDecides}
          groups={[{ heading: null, options: DECIDES.map((o) => ({ ...o, marks: [] })) }]}
          current={{ label: DECIDES.find((o) => o.id === decides)?.label ?? decides, marks: [] }}
          allowNone={false}
          error={null}
        />
        <div>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy || !vrchatRoleId || !discordRoleId}
            onClick={() => {
              act(() => api.addRolePair({ vrchatRoleId, discordRoleId, decides, enabled: true }))
              setVRChatRoleId('')
              setDiscordRoleId('')
            }}
          >
            Pair these roles
          </Button>
        </div>
      </div>
    </div>
  )
}

function PairRow({
  pair,
  busy,
  onSave,
  onDelete,
}: {
  pair: RolePair
  busy: boolean
  onSave: (body: { vrchatRoleId: string; discordRoleId: string; decides: string; enabled: boolean }) => void
  onDelete: () => void
}) {
  const body = (changes: Partial<RolePair>) => ({
    vrchatRoleId: pair.vrchatRoleId,
    discordRoleId: pair.discordRoleId,
    decides: changes.decides ?? pair.decides,
    enabled: changes.enabled ?? pair.enabled,
  })

  return (
    <li
      className="flex flex-wrap items-center gap-3 rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <span className="min-w-0 flex-1 truncate" style={{ fontSize: 'var(--text-small)' }}>
        {pair.vrchatRoleName ?? pair.vrchatRoleId} → {pair.discordRoleName ?? pair.discordRoleId}
      </span>

      <Picker
        label="Who decides"
        value={pair.decides}
        onChange={(decides) => onSave(body({ decides }))}
        groups={[{ heading: null, options: DECIDES.map((o) => ({ ...o, marks: [] })) }]}
        current={{ label: DECIDES.find((o) => o.id === pair.decides)?.label ?? pair.decides, marks: [] }}
        allowNone={false}
        error={null}
        disabled={busy}
      />

      <Switch checked={pair.enabled} onChange={(enabled) => onSave(body({ enabled }))}>
        On
      </Switch>

      <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={onDelete} aria-label="Remove pair">
        <Trash2 className="size-4" />
      </Button>

      <Outcome tone="problem">{pair.problem}</Outcome>
      {!pair.botCanAssign && <Outcome tone="problem">The bot cannot assign that Discord role.</Outcome>}
    </li>
  )
}

function Preview({ preview }: { preview: SyncPreview }) {
  return (
    <div className="flex flex-col gap-2">
      <Fact label="Changes found" value={preview.total.toString()} />
      <Outcome tone="problem">{preview.problem}</Outcome>
      {preview.changes.length > 0 && (
        <ul className="flex max-h-80 flex-col gap-1 overflow-y-auto">
          {preview.changes.map((change, index) => (
            <li key={index} style={{ fontSize: 'var(--text-small)' }}>
              {line(change)}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function line(change: PlannedChange): string {
  const who = change.name ?? change.vrchatUserId ?? change.discordUserId ?? 'Somebody'
  const where = change.platform === 'discord' ? 'Discord' : 'the group'
  const role = change.roleName ?? 'a paired role'

  switch (change.what) {
    case 'ban':
      return `${who} — ban in ${where}`
    case 'unban':
      return `${who} — unban in ${where}`
    case 'remove':
      return `${who} — remove from ${where}`
    case 'role-given':
      return `${who} — give ${role} in ${where}`
    case 'role-taken':
      return `${who} — take away ${role} in ${where}`
    default:
      return `${who} — ${change.why}`
  }
}

function permissions(settings: DiscordSyncSettings): string {
  const held = [
    settings.botCanManageRoles ? 'Manage Roles' : null,
    settings.botCanBanMembers ? 'Ban Members' : null,
    settings.botCanRemoveMembers ? 'Kick Members' : null,
  ].filter(Boolean)

  return held.length ? held.join(', ') : 'None of Manage Roles, Ban Members or Kick Members'
}

function when(at: string | null): string {
  return at ? new Date(at).toLocaleString() : 'Never'
}
