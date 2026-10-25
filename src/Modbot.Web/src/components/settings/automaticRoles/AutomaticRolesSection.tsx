import { useCallback, useEffect, useState } from 'react'
import { Picker, type PickerOption } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { SwitchBank } from '@/components/ui/switch-bank'
import { api, ApiError, type DiscordRole, type GroupRoleRow, type UserSummary } from '@/lib/api'
import {
  automaticRolesApi,
  blankRule,
  conditionLabels,
  ruleInputOf,
  type AutomaticRole,
  type AutomaticRoleInput,
  type ClipRequests,
} from '@/lib/automaticRoles'
import { useDiscordRoles } from '@/lib/discordLists'
import { Checkbox, ConfirmButton, NumberField, Outcome, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { RuleHolders } from './RuleHolders'

const message = (e: unknown, fallback: string) => (e instanceof Error && e.message ? e.message : fallback)

/** A Discord role's colour as `#rrggbb`, or null for a role with none. */
const colourOf = (role: DiscordRole | undefined): string | null =>
  role?.color ? `#${role.color.toString(16).padStart(6, '0')}` : null

/** What the form needs to name a role or a person: the lists the pickers draw from. */
type Lists = {
  discordRoles: DiscordRole[]
  groupRoles: GroupRoleRow[]
  groupRolesError: string | null
  users: UserSummary[]
  usersError: string | null
  /** False only when the server said evidence storage is not set up. */
  evidenceStorage: boolean
}

/**
 * Settings → Automatic roles: the rules that give a VRChat role, a Discord role or one of each
 * while their conditions hold, and which actions ask the moderator's companion for a clip.
 */
export function AutomaticRolesSection() {
  const { data: discord } = useDiscordRoles()
  const [groupRoles, setGroupRoles] = useState<GroupRoleRow[]>([])
  const [groupRolesError, setGroupRolesError] = useState<string | null>(null)
  const [users, setUsers] = useState<UserSummary[]>([])
  const [usersError, setUsersError] = useState<string | null>(null)
  const [evidenceStorage, setEvidenceStorage] = useState(true)

  useEffect(() => {
    api
      .groupRoles()
      .then((list) => setGroupRoles(list.roles))
      .catch(() => setGroupRolesError('Could not load the list.'))

    api
      .users()
      .then(setUsers)
      .catch((e: unknown) =>
        setUsersError(e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : 'Could not load the list.'),
      )

    // When this cannot be read the switches stay usable and the server's own refusal speaks.
    api
      .evidenceSettings()
      .then((s) => setEvidenceStorage(s.backend.backend !== 'None'))
      .catch(() => undefined)
  }, [])

  const lists: Lists = {
    discordRoles: discord?.roles ?? [],
    groupRoles,
    groupRolesError,
    users,
    usersError,
    evidenceStorage,
  }

  return (
    <SettingsSection id="automatic-roles" title="Automatic roles">
      <RulesCard lists={lists} />
      <ClipRequestsCard />
    </SettingsSection>
  )
}

/** Ask for a clip on: bans, kicks, warnings. Each switch saves as it is pressed. */
function ClipRequestsCard() {
  const [value, setValue] = useState<ClipRequests | null>(null)
  const [failed, setFailed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState('')

  const load = useCallback(() => {
    automaticRolesApi
      .clipRequests()
      .then((next) => {
        setValue(next)
        setFailed(false)
      })
      .catch(() => setFailed(true))
  }, [])

  useEffect(load, [load])

  const change = (next: ClipRequests) => {
    setBusy(true)
    setProblem('')
    automaticRolesApi
      .setClipRequests(next)
      .then(setValue)
      .catch((e: unknown) => setProblem(message(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard title="Ask for a clip on" footer={problem ? <Outcome tone="problem">{problem}</Outcome> : undefined}>
      {failed ? (
        <EmptyRow tone="danger" onTryAgain={load}>
          Could not load.
        </EmptyRow>
      ) : !value ? (
        <EmptyRow tone="loading" />
      ) : (
        <>
          <Switch checked={value.bans} disabled={busy} onChange={(bans) => change({ ...value, bans })}>
            Bans
          </Switch>
          <Switch checked={value.kicks} disabled={busy} onChange={(kicks) => change({ ...value, kicks })}>
            Kicks
          </Switch>
          <Switch checked={value.warnings} disabled={busy} onChange={(warnings) => change({ ...value, warnings })}>
            Warnings
          </Switch>
        </>
      )}
    </SettingsCard>
  )
}

/** The rules, listed, with Add rule, edit and delete. */
function RulesCard({ lists }: { lists: Lists }) {
  const [rules, setRules] = useState<AutomaticRole[] | null>(null)
  const [error, setError] = useState('')
  // 'new' for a rule being added, a rule's id for one being changed, null for the list alone.
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [problem, setProblem] = useState('')

  const load = useCallback(() => {
    automaticRolesApi
      .list()
      .then((list) => {
        setRules(list.rules)
        setError('')
      })
      .catch((e: unknown) => setError(message(e, 'Could not load the rules.')))
  }, [])

  useEffect(load, [load])

  const remove = (rule: AutomaticRole) => {
    setProblem('')
    automaticRolesApi
      .remove(rule.id)
      .then(() => {
        setRules((current) => current?.filter((r) => r.id !== rule.id) ?? null)
        if (editing === rule.id) setEditing(null)
      })
      .catch((e: unknown) => setProblem(message(e, 'Could not delete.')))
  }

  const saved = (rule: AutomaticRole) => {
    setRules((current) => {
      if (!current) return [rule]
      return current.some((r) => r.id === rule.id) ? current.map((r) => (r.id === rule.id ? rule : r)) : [...current, rule]
    })
    setEditing(null)
  }

  const editingRule = rules?.find((r) => r.id === editing)

  return (
    <SettingsCard
      span={12}
      title="Rules"
      flush
      footer={
        <>
          <Button size="xs" variant="outline" disabled={editing === 'new'} onClick={() => setEditing('new')}>
            Add rule
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {error ? (
        <EmptyRow tone="danger" onTryAgain={load}>
          {error}
        </EmptyRow>
      ) : !rules ? (
        <EmptyRow tone="loading" />
      ) : (
        <>
          {rules.length === 0 && editing !== 'new' && <EmptyRow>No rules.</EmptyRow>}

          <ul className="flex flex-col">
            {rules.map((rule) => (
              <li
                key={rule.id}
                className="flex flex-col gap-1.5 border-b border-b-(length:--hairline) px-(--panel-pad) py-2 last:border-b-0"
                style={{ fontSize: 'var(--text-small)' }}
              >
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                  <span className="min-w-0 flex-1 font-medium break-words">{rule.name ?? 'Untitled rule'}</span>
                  <div className="flex items-center gap-1">
                    <Button size="xs" variant="ghost" onClick={() => setEditing(rule.id)}>
                      Edit
                    </Button>
                    <ConfirmButton confirm="Delete" onConfirm={() => remove(rule)}>
                      Delete
                    </ConfirmButton>
                  </div>
                </div>

                <div className="flex flex-wrap gap-1.5">
                  {rule.vrChatRoleId && (
                    <Badge variant="secondary">
                      VRChat: {lists.groupRoles.find((r) => r.id === rule.vrChatRoleId)?.name ?? rule.vrChatRoleId}
                    </Badge>
                  )}
                  {rule.discordRoleId && (
                    <DiscordRoleBadge roleId={rule.discordRoleId} roles={lists.discordRoles} prefix="Discord: " />
                  )}
                  <Badge variant="outline">{rule.wholeTeam ? 'Whole team' : 'Chosen roles'}</Badge>
                </div>

                <div className="flex flex-wrap gap-1.5">
                  {conditionLabels(rule).map((label) => (
                    <Badge key={label} variant="info">
                      {label}
                    </Badge>
                  ))}
                </div>

                <RuleHolders ruleId={rule.id} />
              </li>
            ))}
          </ul>

          {editing !== null && (
            <RuleForm
              // A new form for each rule, so nothing typed for one carries into the next.
              key={editing}
              lists={lists}
              rule={editingRule}
              onSaved={saved}
              onCancel={() => setEditing(null)}
            />
          )}
        </>
      )}
    </SettingsCard>
  )
}

function DiscordRoleBadge({ roleId, roles, prefix = '' }: { roleId: string; roles: DiscordRole[]; prefix?: string }) {
  const role = roles.find((r) => r.id === roleId)
  const colour = colourOf(role)

  return (
    <Badge variant="secondary">
      {colour && <span className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: colour }} aria-hidden />}
      {prefix}
      {role?.name ?? roleId}
    </Badge>
  )
}

/** The add and change form. A change starts from the saved rule; Save refuses with the server's own sentence. */
function RuleForm({
  lists,
  rule,
  onSaved,
  onCancel,
}: {
  lists: Lists
  rule: AutomaticRole | undefined
  onSaved: (rule: AutomaticRole) => void
  onCancel: () => void
}) {
  const [form, setForm] = useState<AutomaticRoleInput>(() => (rule ? ruleInputOf(rule) : blankRule()))
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState('')

  const set = (patch: Partial<AutomaticRoleInput>) => setForm((current) => ({ ...current, ...patch }))

  const save = () => {
    setBusy(true)
    setProblem('')

    const done = rule ? automaticRolesApi.change(rule.id, form) : automaticRolesApi.add(form)
    done
      .then(onSaved)
      .catch((e: unknown) => setProblem(message(e, 'Could not save.')))
      .finally(() => setBusy(false))
  }

  const groupRoleOptions: PickerOption[] = lists.groupRoles
    .filter((r) => !r.isDefault)
    .map((r) => ({ id: r.id, label: r.name ?? r.id, keywords: r.id, marks: [] }))

  const groupRoleName = (id: string) => lists.groupRoles.find((r) => r.id === id)?.name ?? id

  const groupRolePicker = (
    label: string,
    value: string,
    onChange: (id: string) => void,
    allowNone: boolean,
    taken: string[] = [],
  ) => (
    <Picker
      label={label}
      value={value}
      onChange={onChange}
      groups={groupRoleOptions.length ? [{ heading: null, options: groupRoleOptions.filter((o) => !taken.includes(o.id)) }] : []}
      current={value ? { label: groupRoleName(value), marks: [] } : null}
      allowNone={allowNone}
      error={lists.groupRolesError}
    />
  )

  const userName = (id: string) => lists.users.find((u) => u.id === id)?.username ?? id

  const userOptions: PickerOption[] = lists.users
    .filter((u) => !u.isDeleted && !form.takeBackUserIds.includes(u.id))
    .map((u) => ({ id: u.id, label: u.username, keywords: u.id, marks: [] }))

  const without = (ids: string[], id: string) => ids.filter((i) => i !== id)
  const withId = (ids: string[], id: string) => (!id || ids.includes(id) ? ids : [...ids, id])

  const needsStorage = !lists.evidenceStorage

  return (
    <div className="flex flex-col gap-4 border-t border-t-(length:--hairline) p-(--panel-pad)">
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Name</span>
        <Input value={form.name ?? ''} maxLength={100} onChange={(e) => set({ name: e.target.value || null })} />
      </label>

      <div className="grid gap-3 sm:grid-cols-2">
        {groupRolePicker('VRChat role', form.vrChatRoleId ?? '', (id) => set({ vrChatRoleId: id || null }), true)}
        <RolePicker
          label="Discord role"
          value={form.discordRoleId ?? ''}
          needsAssign
          onChange={(id) => set({ discordRoleId: id || null })}
        />
      </div>

      <div className="flex flex-col gap-2">
        <SwitchBank
          label="Applies to"
          value={form.wholeTeam ? 'team' : 'chosen'}
          options={[
            { value: 'team', label: 'Whole team' },
            { value: 'chosen', label: 'Chosen roles' },
          ]}
          onChange={(next) => set({ wholeTeam: next === 'team' })}
        />

        {!form.wholeTeam && (
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-2">
              <RolePicker
                label="Add a Discord role"
                value=""
                allowNone={false}
                onChange={(id) => set({ whoDiscordRoleIds: withId(form.whoDiscordRoleIds, id) })}
              />
              <Chips
                ids={form.whoDiscordRoleIds}
                name={(id) => lists.discordRoles.find((r) => r.id === id)?.name ?? id}
                colour={(id) => colourOf(lists.discordRoles.find((r) => r.id === id))}
                onRemove={(id) => set({ whoDiscordRoleIds: without(form.whoDiscordRoleIds, id) })}
              />
            </div>
            <div className="flex flex-col gap-2">
              {groupRolePicker(
                'Add a VRChat role',
                '',
                (id) => set({ whoVRChatRoleIds: withId(form.whoVRChatRoleIds, id) }),
                false,
                form.whoVRChatRoleIds,
              )}
              <Chips
                ids={form.whoVRChatRoleIds}
                name={groupRoleName}
                onRemove={(id) => set({ whoVRChatRoleIds: without(form.whoVRChatRoleIds, id) })}
              />
            </div>
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <Switch checked={form.companionRunning} onChange={(companionRunning) => set({ companionRunning })}>
          Companion running
        </Switch>

        <Switch checked={form.clipsReady} disabled={needsStorage} onChange={(clipsReady) => set({ clipsReady })}>
          Clips ready
        </Switch>

        <Switch checked={form.inGroupInstance} onChange={(inGroupInstance) => set({ inGroupInstance })}>
          In a group instance
        </Switch>

        <Switch checked={form.minimumLevel} onChange={(minimumLevel) => set({ minimumLevel })}>
          Minimum level
        </Switch>
        {form.minimumLevel && (
          <div className="flex flex-col gap-2 pl-2 sm:grid sm:grid-cols-2 sm:items-end">
            <Checkbox
              checked={form.minimumLevelPassedTrial}
              onChange={(minimumLevelPassedTrial) => set({ minimumLevelPassedTrial })}
            >
              Passed the staff trial
            </Checkbox>
            <RolePicker
              label="Staff role"
              value={form.minimumLevelStaffRoleId ?? ''}
              onChange={(id) => set({ minimumLevelStaffRoleId: id || null })}
            />
          </div>
        )}

        <Switch checked={form.overturnedBans} onChange={(overturnedBans) => set({ overturnedBans })}>
          Overturned bans
        </Switch>
        {form.overturnedBans && (
          <div className="grid grid-cols-2 gap-3 pl-2">
            <NumberField
              label="At most"
              min={0}
              value={numberText(form.overturnedBansMax)}
              onChange={(v) => set({ overturnedBansMax: toNumber(v) })}
            />
            <NumberField
              label="In days"
              min={1}
              value={numberText(form.overturnedBansDays)}
              onChange={(v) => set({ overturnedBansDays: toNumber(v) })}
            />
          </div>
        )}

        <Switch checked={form.dailyBanCap} onChange={(dailyBanCap) => set({ dailyBanCap })}>
          Daily ban cap
        </Switch>
        {form.dailyBanCap && (
          <div className="grid grid-cols-2 gap-3 pl-2">
            <NumberField
              label="Bans a day"
              min={1}
              value={numberText(form.dailyBanCapMax)}
              onChange={(v) => set({ dailyBanCapMax: toNumber(v) })}
            />
          </div>
        )}

        <Switch checked={form.missedClips} disabled={needsStorage} onChange={(missedClips) => set({ missedClips })}>
          Missing clips
        </Switch>
        {form.missedClips && (
          <div className="grid grid-cols-2 gap-3 pl-2">
            <NumberField
              label="At most"
              min={0}
              value={numberText(form.missedClipsMax)}
              onChange={(v) => set({ missedClipsMax: toNumber(v) })}
            />
            <NumberField
              label="In days"
              min={1}
              value={numberText(form.missedClipsDays)}
              onChange={(v) => set({ missedClipsDays: toNumber(v) })}
            />
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <Picker
          label="Take back from"
          value=""
          onChange={(id) => set({ takeBackUserIds: withId(form.takeBackUserIds, id) })}
          groups={userOptions.length ? [{ heading: null, options: userOptions }] : []}
          current={null}
          allowNone={false}
          error={lists.usersError}
        />
        <Chips
          ids={form.takeBackUserIds}
          name={userName}
          onRemove={(id) => set({ takeBackUserIds: without(form.takeBackUserIds, id) })}
        />
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Button size="sm" disabled={busy} onClick={save}>
          {rule ? 'Save' : 'Add rule'}
        </Button>
        <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>
          Cancel
        </Button>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

const numberText = (n: number | null) => (n === null ? '' : String(n))
const toNumber = (text: string) => (text.trim() === '' ? null : Number(text))

/** Chosen things as removable chips; a Discord role keeps its own colour. */
function Chips({
  ids,
  name,
  colour,
  onRemove,
}: {
  ids: string[]
  name: (id: string) => string
  colour?: (id: string) => string | null
  onRemove: (id: string) => void
}) {
  if (ids.length === 0) return null

  return (
    <div className="flex flex-wrap gap-1.5">
      {ids.map((id) => {
        const dot = colour?.(id)

        return (
          <Badge key={id} variant="secondary" className="gap-1 pr-1">
            {dot && <span className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: dot }} aria-hidden />}
            {name(id)}
            <Button
              size="xs"
              variant="ghost"
              className="h-4 w-4 p-0"
              aria-label={`Remove ${name(id)}`}
              onClick={() => onRemove(id)}
            >
              ×
            </Button>
          </Badge>
        )
      })}
    </div>
  )
}
