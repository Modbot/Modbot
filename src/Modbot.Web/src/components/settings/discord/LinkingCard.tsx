import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type DiscordLinkingSettings } from '@/lib/api'
import { Fact, Field, Outcome, PasswordField, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The backup channel gets one plain message with a button. */
const MENTION_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

/**
 * Settings → Discord → Account linking (Discord account linking design §4): the OAuth client that
 * "Sign in with Discord" uses, the two links built from it, the prompt for new joiners, and the
 * roles a linked member is given.
 *
 * Its own card with its own Save, because it saves to its own endpoint. The secret is write-only:
 * a blank field keeps the stored one.
 */
export function LinkingCard() {
  const [data, setData] = useState<DiscordLinkingSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordLinkingSettings()
        .then(setData)
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load account linking settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Account linking">
        <EmptyRow className="px-0" tone={error ? 'danger' : undefined}>{error ?? 'Loading…'}</EmptyRow>
      </SettingsCard>
    )
  }

  return <LinkingForm settings={data} onSaved={setData} />
}

function LinkingForm({
  settings,
  onSaved,
}: {
  settings: DiscordLinkingSettings
  onSaved: (next: DiscordLinkingSettings) => void
}) {
  const [clientId, setClientId] = useState(settings.clientId ?? '')
  const [clientSecret, setClientSecret] = useState('')
  const [prompt, setPrompt] = useState(settings.promptNewMembers)
  const [backupChannelId, setBackupChannelId] = useState(settings.backupChannelId ?? '')
  const [linkedRoleId, setLinkedRoleId] = useState(settings.linkedRoleId ?? '')
  const [eighteenPlusRoleId, setEighteenPlusRoleId] = useState(settings.eighteenPlusRoleId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordLinkingSettings({
        clientId,
        clientSecret,
        removeClientSecret: false,
        promptNewMembers: prompt,
        backupChannelId,
        linkedRoleId,
        eighteenPlusRoleId,
      })
      .then((next) => {
        onSaved(next)
        setClientSecret('')
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Account linking"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save account linking'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid gap-3 sm:grid-cols-2">
        <Fact label="Linking" value={settings.available ? 'On' : 'Not set up'} />
        <Fact label="Client secret" value={settings.clientSecretStored ? 'Stored' : 'Not set'} />
      </div>

      <div className="flex max-w-lg flex-col gap-3">
        <Field label="OAuth client id" value={clientId} onChange={setClientId} placeholder="" />
        <PasswordField label="OAuth client secret" value={clientSecret} onChange={setClientSecret} />
        <CopyValue label="Redirect URL" value={settings.redirectUrl} />
        <CopyValue label="Bot invite link" value={settings.inviteUrl} open />
      </div>

      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked={prompt} onChange={setPrompt}>
          Prompt new joiners to link their VRChat account
        </Switch>
        <ChannelPicker
          label="Backup channel"
          value={backupChannelId}
          onChange={setBackupChannelId}
          needs={MENTION_NEEDS}
        />
        <RolePicker label="Linked role" value={linkedRoleId} onChange={setLinkedRoleId} needsAssign />
        <RolePicker label="18+ role" value={eighteenPlusRoleId} onChange={setEighteenPlusRoleId} needsAssign />
      </div>
    </SettingsCard>
  )
}

/** A value to paste somewhere else, with a Copy button. Absent values are not shown. */
function CopyValue({ label, value, open = false }: { label: string; value: string | null; open?: boolean }) {
  const [copied, setCopied] = useState(false)

  if (!value) return null

  const copy = () => {
    void navigator.clipboard?.writeText(value).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <div className="flex items-center gap-2">
        <code
          className="min-w-0 flex-1 truncate rounded-sm border border-(length:--hairline) border-input bg-strip px-2.5 font-mono leading-(--control-h) select-all"
          style={{ height: 'var(--control-h)' }}
          title={value}
        >
          {value}
        </code>
        <Button type="button" variant="outline" size="sm" onClick={copy}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
        {open && (
          <Button asChild type="button" variant="ghost" size="sm">
            <a href={value} target="_blank" rel="noopener noreferrer">
              Open
            </a>
          </Button>
        )}
      </div>
    </div>
  )
}
