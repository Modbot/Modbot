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
  type DiscordWelcomeSettings,
  type DiscordWelcomeVia,
} from '@/lib/api'
import { Field, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The welcome is a plain message that pings the member, nothing more. */
const WELCOME_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

const WAYS: { value: DiscordWelcomeVia; label: string }[] = [
  { value: 'dm', label: 'Direct message' },
  { value: 'channel', label: 'Channel' },
  { value: 'both', label: 'Both' },
]

/** One row being edited. `key` is only for React; a rule has a role and a default does not. */
type Row = { key: number; roleId: string; text: string; link: string }

let lastKey = 0

function newRow(roleId = '', text = '', link: string | null = null): Row {
  lastKey += 1
  return { key: lastKey, roleId, text, link: link ?? '' }
}

/**
 * Settings → Discord → Welcome steps (smart welcome design §7): whether new members are sent
 * next steps, where they go, the rules that pick a step from a role the member holds, and the
 * default steps that fill up to three.
 *
 * Its own card with its own Save, because it saves to its own endpoint, like Join gate.
 */
export function WelcomeCard() {
  const [data, setData] = useState<DiscordWelcomeSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordWelcomeSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the welcome steps.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Welcome steps">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <WelcomeForm settings={data} onSaved={setData} />
}

function WelcomeForm({
  settings,
  onSaved,
}: {
  settings: DiscordWelcomeSettings
  onSaved: (next: DiscordWelcomeSettings) => void
}) {
  const [enabled, setEnabled] = useState(settings.enabled)
  const [via, setVia] = useState<DiscordWelcomeVia>(settings.via)
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [linkButton, setLinkButton] = useState(settings.linkButton)
  const [rules, setRules] = useState<Row[]>(() => settings.rules.map((r) => newRow(r.roleId, r.text, r.link)))
  const [defaults, setDefaults] = useState<Row[]>(() => settings.defaults.map((d) => newRow('', d.text, d.link)))

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordWelcomeSettings({
        enabled,
        via,
        channelId: via === 'dm' ? '' : channelId,
        linkButton,
        rules: rules.map((r) => ({ roleId: r.roleId, text: r.text, link: r.link.trim() || null })),
        defaults: defaults.map((d) => ({ text: d.text, link: d.link.trim() || null })),
      })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Welcome steps"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save welcome steps'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked={enabled} onChange={setEnabled}>
          Welcome new members
        </Switch>
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Send as</span>
          <Select value={via} onChange={(v) => setVia(v as DiscordWelcomeVia)} aria-label="Send as">
            {WAYS.map((w) => (
              <option key={w.value} value={w.value}>
                {w.label}
              </option>
            ))}
          </Select>
        </label>
        {via !== 'dm' && (
          <ChannelPicker label="Welcome channel" value={channelId} onChange={setChannelId} needs={WELCOME_NEEDS} />
        )}
        <Switch checked={linkButton} onChange={setLinkButton}>
          Add a Link your VRChat button
        </Switch>
      </div>

      <StepList
        title="Rules"
        addLabel="Add rule"
        rows={rules}
        most={settings.mostRules}
        textLength={settings.mostTextLength}
        withRole
        onChange={setRules}
        onAdd={() => setRules([...rules, newRow()])}
      />

      <StepList
        title="Default steps"
        addLabel="Add default step"
        rows={defaults}
        most={settings.mostDefaults}
        textLength={settings.mostTextLength}
        onChange={setDefaults}
        onAdd={() => setDefaults([...defaults, newRow()])}
      />
    </SettingsCard>
  )
}

function StepList({
  title,
  addLabel,
  rows,
  most,
  textLength,
  withRole = false,
  onChange,
  onAdd,
}: {
  title: string
  addLabel: string
  rows: Row[]
  most: number
  textLength: number
  withRole?: boolean
  onChange: (rows: Row[]) => void
  onAdd: () => void
}) {
  const edit = (key: number, patch: Partial<Row>) => onChange(rows.map((r) => (r.key === key ? { ...r, ...patch } : r)))

  const move = (index: number, by: -1 | 1) => {
    const next = [...rows]
    const [row] = next.splice(index, 1)
    next.splice(index + by, 0, row)
    onChange(next)
  }

  return (
    <div className="flex max-w-lg flex-col gap-3">
      <h3 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
        {title}
      </h3>

      {rows.map((row, i) => (
        <div key={row.key} className="flex flex-col gap-3 rounded-sm border border-(length:--hairline) border-border p-3">
          {withRole && (
            <RolePicker
              label="Role"
              value={row.roleId}
              onChange={(roleId) => edit(row.key, { roleId })}
              allowNone={false}
            />
          )}
          <Field label="Step" value={row.text} maxLength={textLength} onChange={(text) => edit(row.key, { text })} />
          <Field label="Link" mono value={row.link} onChange={(link) => edit(row.key, { link })} placeholder="https://" />
          <div className="flex items-center gap-1">
            <Button type="button" variant="ghost" size="icon-sm" disabled={i === 0} onClick={() => move(i, -1)}>
              <ArrowUp className="size-4" />
              <span className="sr-only">Move up</span>
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              disabled={i === rows.length - 1}
              onClick={() => move(i, 1)}
            >
              <ArrowDown className="size-4" />
              <span className="sr-only">Move down</span>
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              onClick={() => onChange(rows.filter((r) => r.key !== row.key))}
            >
              <Trash2 className="size-4" />
              <span className="sr-only">Remove</span>
            </Button>
          </div>
        </div>
      ))}

      <div>
        <Button type="button" variant="outline" size="sm" disabled={rows.length >= most} onClick={onAdd}>
          <Plus className="size-4" />
          {addLabel}
        </Button>
      </div>
    </div>
  )
}
