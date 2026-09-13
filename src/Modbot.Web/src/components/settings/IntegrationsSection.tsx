import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type OnboardingStatus } from '@/lib/api'
import { Checkbox, Fact, Field, Hint, Outcome, PasswordField, Placeholder } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'

/**
 * Discord and SMTP — spec 7.1 step 5, re-run.
 *
 * Neither is validated by connecting, here or in the wizard. A Discord token is checked by
 * starting a gateway session and SMTP by sending mail; both are slow, both fail for reasons
 * unrelated to the value being wrong, and neither is worth blocking a settings save on.
 */
export function IntegrationsSection({
  status,
  refresh,
}: {
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  return (
    <SettingsSection
      id="integrations"
      title="Integrations"
      description="Optional services Modbot can talk to. Both are saved together, and neither is tested by connecting."
    >
      {/* Mounted only once the status is in hand -- see VRChatSection for why. */}
      {status ? (
        <IntegrationsForm status={status} refresh={refresh} />
      ) : (
        <Placeholder>Loading…</Placeholder>
      )}
    </SettingsSection>
  )
}

function IntegrationsForm({
  status,
  refresh,
}: {
  status: OnboardingStatus
  refresh: () => Promise<void>
}) {
  const [botToken, setBotToken] = useState('')
  const [guildId, setGuildId] = useState(status.integrations.discordGuildId ?? '')
  const [host, setHost] = useState(status.integrations.smtpHost ?? '')
  const [port, setPort] = useState('')
  const [smtpUsername, setSmtpUsername] = useState('')
  const [smtpPassword, setSmtpPassword] = useState('')
  const [fromAddress, setFromAddress] = useState('')
  const [useTls, setUseTls] = useState(true)

  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setSaved(false)
    setError(null)

    api
      .saveIntegrations({
        // Omitted when untouched, sent empty to clear. A blank secret field means "leave it
        // alone", because the alternative is that opening this page and pressing Save silently
        // disconnects Discord.
        discord: {
          ...(botToken ? { botToken } : {}),
          guildId,
        },
        smtp: {
          host,
          ...(port ? { port: Number(port) } : {}),
          username: smtpUsername,
          ...(smtpPassword ? { password: smtpPassword } : {}),
          fromAddress,
          useTls,
        },
      })
      .then(async () => {
        setSaved(true)
        setBotToken('')
        setSmtpPassword('')
        await refresh()
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not save.'),
      )
      .finally(() => setSaving(false))
  }

  // One form around both cards, because the API saves them as one body. `contents` keeps the
  // form element out of the layout so the cards stay direct children of the grid.
  return (
    <form onSubmit={save} className="contents">
      <SettingsCard
        title="Discord"
        description="Optional. Without it the bot simply does not start and nothing else about Modbot changes."
      >
        <Fact
          label="Bot"
          value={status.integrations.discordConfigured ? 'Token stored' : 'Not configured'}
        />
        <Hint>
          Secrets are encrypted at rest and never read back, so the token field is blank even when
          one is stored — leave it blank to keep the one you have.
        </Hint>
        <div className="flex max-w-sm flex-col gap-3">
          <PasswordField label="Bot token" value={botToken} onChange={setBotToken} />
          <Field label="Guild id" value={guildId} onChange={setGuildId} placeholder="" />
        </div>
      </SettingsCard>

      <SettingsCard
        title="Email (SMTP)"
        description="Optional. Operator-supplied, with no hosted provider in the middle."
      >
        <Fact
          label="Relay"
          value={
            status.integrations.smtpConfigured
              ? (status.integrations.smtpHost ?? 'Configured')
              : 'Not configured'
          }
        />
        <Hint>Without it, notifications fall back to the surfaces that remain.</Hint>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Host" value={host} onChange={setHost} placeholder="smtp.example.com" />
          <Field label="Port" value={port} onChange={setPort} placeholder="587" />
          <Field
            label="From address"
            value={fromAddress}
            onChange={setFromAddress}
            placeholder="modbot@example.com"
          />
          <Field label="Username" value={smtpUsername} onChange={setSmtpUsername} placeholder="" />
          <PasswordField label="Password" value={smtpPassword} onChange={setSmtpPassword} />
        </div>
        <Checkbox checked={useTls} onChange={setUseTls}>
          Use TLS
        </Checkbox>
      </SettingsCard>

      <div className="col-span-12 flex flex-wrap items-center gap-3">
        <Button type="submit" size="sm" disabled={saving}>
          {saving ? 'Saving…' : 'Save integrations'}
        </Button>
        <Outcome tone="ok">{saved && 'Saved. Nothing was tested by connecting — see above.'}</Outcome>
        <Outcome tone="problem">{error}</Outcome>
      </div>
    </form>
  )
}
