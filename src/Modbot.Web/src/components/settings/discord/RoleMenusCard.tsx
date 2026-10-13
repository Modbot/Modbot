import { useCallback, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import {
  api,
  ApiError,
  type DiscordChannelPermission,
  type DiscordRoleMenu,
  type DiscordRoleMenuInput,
  type DiscordRoleMenus,
  type DiscordRoleMenuStyle,
} from '@/lib/api'
import { useDiscordRoles } from '@/lib/discordLists'
import { ConfirmButton, Field, LongField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** A role menu is a plain message with buttons or a list under it. */
const MENU_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

const STYLES: { value: DiscordRoleMenuStyle; label: string }[] = [
  { value: 'buttons', label: 'Buttons' },
  { value: 'select', label: 'List' },
]

/** One role row being edited. `key` is only for React. */
type Row = { key: number; roleId: string; label: string; emoji: string }

let lastKey = 0

function newRow(roleId = '', label = '', emoji: string | null = null): Row {
  lastKey += 1
  return { key: lastKey, roleId, label, emoji: emoji ?? '' }
}

/** A menu being edited: the fields as typed. `menu` is null for one not yet saved. */
type Draft = {
  name: string
  channelId: string
  text: string
  style: DiscordRoleMenuStyle
  singleChoice: boolean
  rows: Row[]
}

function draftOf(menu: DiscordRoleMenu | null): Draft {
  return {
    name: menu?.name ?? '',
    channelId: menu?.channelId ?? '',
    text: menu?.text ?? '',
    style: menu?.style ?? 'buttons',
    singleChoice: menu?.singleChoice ?? false,
    rows: (menu?.roles ?? []).map((r) => newRow(r.roleId, r.label, r.emoji)),
  }
}

/** What goes to the server. A blank label is the role's own name. */
function inputOf(draft: Draft, nameOf: (roleId: string) => string): DiscordRoleMenuInput {
  return {
    name: draft.name,
    channelId: draft.channelId,
    text: draft.text,
    style: draft.style,
    singleChoice: draft.singleChoice,
    roles: draft.rows.map((r) => ({
      roleId: r.roleId,
      label: r.label.trim() || nameOf(r.roleId),
      emoji: r.emoji.trim() || null,
    })),
  }
}

/** Menus on the page: the saved ones, and any new one not saved yet (`menu` null). */
type Item = { key: number; menu: DiscordRoleMenu | null }

/**
 * Settings → Discord → Role menus: messages Modbot posts with buttons or a list, where a member
 * picks a role to get it or drop it. Saving changes Modbot's copy; Post writes the message.
 *
 * Each menu saves to its own endpoint, so each has its own buttons.
 */
export function RoleMenusCard() {
  const [data, setData] = useState<DiscordRoleMenus | null>(null)
  const [items, setItems] = useState<Item[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordRoleMenus()
        .then((next) => {
          setData(next)
          setItems(next.menus.map((menu) => ({ key: ++lastKey, menu })))
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the role menus.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Role menus">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return (
    <SettingsCard title="Role menus" span={12}>
      {items.length === 0 && <EmptyRow className="px-0">No role menus.</EmptyRow>}

      {items.map((item) => (
        <MenuEditor
          key={item.key}
          menu={item.menu}
          limits={data}
          onSaved={(menu) => setItems((all) => all.map((i) => (i.key === item.key ? { ...i, menu } : i)))}
          onGone={() => setItems((all) => all.filter((i) => i.key !== item.key))}
        />
      ))}

      <div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={items.length >= data.mostMenus}
          onClick={() => setItems([...items, { key: ++lastKey, menu: null }])}
        >
          <Plus className="size-4" />
          Add role menu
        </Button>
      </div>
    </SettingsCard>
  )
}

function MenuEditor({
  menu,
  limits,
  onSaved,
  onGone,
}: {
  menu: DiscordRoleMenu | null
  limits: DiscordRoleMenus
  onSaved: (menu: DiscordRoleMenu) => void
  onGone: () => void
}) {
  const { data: roleList } = useDiscordRoles()
  const nameOf = useCallback(
    (roleId: string) => roleList?.roles.find((r) => r.id === roleId)?.name ?? '',
    [roleList],
  )

  const [draft, setDraft] = useState<Draft>(() => draftOf(menu))
  const [busy, setBusy] = useState<'save' | 'post' | 'delete' | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const posted = menu?.messageId != null
  const edit = (patch: Partial<Draft>) => setDraft((d) => ({ ...d, ...patch }))

  const input = inputOf(draft, nameOf)
  const unsaved = menu === null || JSON.stringify(input) !== JSON.stringify(inputOf(draftOf(menu), nameOf))

  const editRow = (key: number, patch: Partial<Row>) => edit({ rows: draft.rows.map((r) => (r.key === key ? { ...r, ...patch } : r)) })

  // Picking a role fills the label with its name, unless the label was typed by hand.
  const pickRole = (row: Row, roleId: string) => {
    const typedByHand = row.label !== '' && row.label !== nameOf(row.roleId)
    editRow(row.key, { roleId, label: typedByHand ? row.label : nameOf(roleId) })
  }

  const move = (index: number, by: -1 | 1) => {
    const next = [...draft.rows]
    const [row] = next.splice(index, 1)
    next.splice(index + by, 0, row)
    edit({ rows: next })
  }

  const run = (what: 'save' | 'post' | 'delete', work: () => Promise<unknown>, done: string | null, fail: string) => {
    setBusy(what)
    setSaved(null)
    setProblem(null)

    work()
      .then(() => setSaved(done))
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : fail))
      .finally(() => setBusy(null))
  }

  const save = () =>
    run(
      'save',
      () =>
        (menu ? api.setDiscordRoleMenu(menu.id, input) : api.createDiscordRoleMenu(input)).then((next) => {
          onSaved(next)
          setDraft(draftOf(next))
        }),
      'Saved.',
      'Could not save.',
    )

  const post = () =>
    menu &&
    run(
      'post',
      () => api.postDiscordRoleMenu(menu.id).then(onSaved),
      posted ? 'Updated.' : 'Posted.',
      'Could not post.',
    )

  const remove = () => menu && run('delete', () => api.deleteDiscordRoleMenu(menu.id).then(onGone), null, 'Could not delete.')

  return (
    <div className="flex max-w-lg flex-col gap-3 rounded-sm border border-(length:--hairline) border-border p-3">
      <Field label="Name" value={draft.name} maxLength={limits.mostNameLength} onChange={(name) => edit({ name })} />
      <ChannelPicker
        label="Channel"
        value={draft.channelId}
        onChange={(channelId) => edit({ channelId })}
        needs={MENU_NEEDS}
        allowNone={false}
        disabled={posted}
      />
      <LongField label="Message" placeholder="" rows={3} value={draft.text} onChange={(text) => edit({ text })} />
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Style</span>
        <Select value={draft.style} onChange={(v) => edit({ style: v as DiscordRoleMenuStyle })} aria-label="Style">
          {STYLES.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
        </Select>
      </label>
      <Switch checked={draft.singleChoice} onChange={(singleChoice) => edit({ singleChoice })}>
        Pick one
      </Switch>

      <h3 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        Roles
      </h3>

      {draft.rows.map((row, i) => (
        <div key={row.key} className="flex flex-col gap-3 rounded-sm border border-(length:--hairline) border-border p-3">
          <RolePicker
            label="Role"
            value={row.roleId}
            onChange={(roleId) => pickRole(row, roleId)}
            allowNone={false}
            needsAssign
            hideDangerous
          />
          <Field
            label="Label"
            value={row.label}
            maxLength={limits.mostLabelLength}
            onChange={(label) => editRow(row.key, { label })}
          />
          <Field label="Emoji" value={row.emoji} onChange={(emoji) => editRow(row.key, { emoji })} />
          <div className="flex items-center gap-1">
            <Button type="button" variant="ghost" size="icon-sm" disabled={i === 0} onClick={() => move(i, -1)}>
              <ArrowUp className="size-4" />
              <span className="sr-only">Move up</span>
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              disabled={i === draft.rows.length - 1}
              onClick={() => move(i, 1)}
            >
              <ArrowDown className="size-4" />
              <span className="sr-only">Move down</span>
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              onClick={() => edit({ rows: draft.rows.filter((r) => r.key !== row.key) })}
            >
              <Trash2 className="size-4" />
              <span className="sr-only">Remove</span>
            </Button>
          </div>
        </div>
      ))}

      <div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={draft.rows.length >= limits.mostRoles}
          onClick={() => edit({ rows: [...draft.rows, newRow()] })}
        >
          <Plus className="size-4" />
          Add role
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="xs" onClick={save} disabled={busy !== null || !unsaved}>
          {busy === 'save' ? 'Saving…' : 'Save'}
        </Button>
        <Button type="button" size="xs" variant="outline" onClick={post} disabled={busy !== null || menu === null || unsaved}>
          {busy === 'post' ? 'Posting…' : posted ? 'Update' : 'Post'}
        </Button>
        {menu && (
          <ConfirmButton confirm="Delete" disabled={busy !== null} onConfirm={remove}>
            Delete
          </ConfirmButton>
        )}
        {!menu && (
          <Button type="button" size="xs" variant="ghost" onClick={onGone}>
            Discard
          </Button>
        )}
        {menu?.changedSincePosted && <Outcome tone="problem">Changed since posted</Outcome>}
        <Outcome tone="ok">{saved}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}
