import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type OnboardingStatus } from '@/lib/api'
import { Field, PasswordField, Row, Section } from './fields'

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

  return (
    <form onSubmit={save} className="flex flex-col gap-4">
      <Section title="Discord">
        <Row
          label="Bot"
          value={status.integrations.discordConfigured ? 'Token stored' : 'Not configured'}
        />
        <p className="mt-1 mb-3 max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Optional. Without it the bot simply does not start and nothing else about Modbot
          changes. Secrets are encrypted at rest and never read back, so the field below is blank
          even when a token is stored — leave it blank to keep the one you have.
        </p>
        <div className="grid gap-3 sm:grid-cols-2">
          <PasswordField label="Bot token" value={botToken} onChange={setBotToken} />
          <Field label="Guild id" value={guildId} onChange={setGuildId} placeholder="" />
        </div>
      </Section>

      <Section title="Email (SMTP)">
        <Row
          label="Relay"
          value={status.integrations.smtpConfigured ? (status.integrations.smtpHost ?? 'Configured') : 'Not configured'}
        />
        <p className="mt-1 mb-3 max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Operator-supplied, with no hosted provider in the middle. Optional: without it,
          notifications fall back to the surfaces that remain.
        </p>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Field label="Host" value={host} onChange={setHost} placeholder="smtp.example.com" />
          <Field label="Port" value={port} onChange={setPort} placeholder="587" />
          <Field label="From address" value={fromAddress} onChange={setFromAddress} placeholder="modbot@example.com" />
          <Field label="Username" value={smtpUsername} onChange={setSmtpUsername} placeholder="" />
          <PasswordField label="Password" value={smtpPassword} onChange={setSmtpPassword} />
        </div>
        <label className="mt-3 flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
          <input type="checkbox" checked={useTls} onChange={(e) => setUseTls(e.target.checked)} />
          Use TLS
        </label>
      </Section>

      <div className="flex items-center gap-3">
        <Button type="submit" size="sm" disabled={saving}>
          {saving ? 'Saving…' : 'Save integrations'}
        </Button>
        {saved && (
          <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>
            Saved. Nothing was tested by connecting — see above.
          </span>
        )}
        {error && (
          <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {error}
          </span>
        )}
      </div>
    </form>
  )
}
