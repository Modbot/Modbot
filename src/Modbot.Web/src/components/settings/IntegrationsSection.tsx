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
      description="Optional services Modbot can talk to, and the address they send people back to. All saved together; nothing is tested by connecting."
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
  const [publicAddress, setPublicAddress] = useState(status.integrations.publicAddress ?? '')

  const [testTo, setTestTo] = useState('')
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ sent: boolean; error: string | null } | null>(null)

  // Proves the *saved* settings work. Save first, then test: a wrong relay setting should be
  // found here, not by a moderator who cannot get back in.
  const sendTest = () => {
    setTesting(true)
    setTestResult(null)
    api
      .sendTestEmail(testTo)
      .then(setTestResult)
      .catch((e: unknown) =>
        setTestResult({ sent: false, error: e instanceof ApiError ? e.message : 'Could not reach the server.' }),
      )
      .finally(() => setTesting(false))
  }

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
        // Sent as typed: empty clears it, which stops reset links being sent until it is set again.
        publicAddress,
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
        <div className="mt-4 flex flex-wrap items-end gap-3">
          <div className="min-w-[16rem]">
            <Field label="Send a test message to" value={testTo} onChange={setTestTo} placeholder="you@example.com" />
          </div>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={testing || !testTo.trim() || !status.integrations.smtpConfigured}
            onClick={sendTest}
          >
            {testing ? 'Sending…' : 'Send a test email'}
          </Button>
          <Outcome tone="ok">{testResult?.sent && 'Sent. Check the inbox.'}</Outcome>
          <Outcome tone="problem">{testResult && !testResult.sent ? testResult.error : null}</Outcome>
        </div>
      </SettingsCard>

      <SettingsCard
        title="Public address"
        description="The address people use to reach this Modbot."
      >
        <Fact
          label="Address"
          value={status.integrations.publicAddress ?? 'Not set — reset links cannot be sent'}
        />
        <Hint>
          Reset links sent by email or Discord are built from this and from nothing else — never
          from the address a request came in on, which anyone can forge. Just the start of the
          address, with no path.
        </Hint>
        <div className="max-w-sm">
          <Field
            label="Public address"
            value={publicAddress}
            onChange={setPublicAddress}
            placeholder={status.integrations.publicAddressSuggestion ?? window.location.origin}
          />
        </div>
        {status.integrations.publicAddressSuggestion && !publicAddress && (
          <button
            type="button"
            className="mt-2 text-primary underline-offset-2 hover:underline"
            style={{ fontSize: 'var(--text-small)' }}
            onClick={() => setPublicAddress(status.integrations.publicAddressSuggestion ?? '')}
          >
            Use {status.integrations.publicAddressSuggestion}, which is what the host says it is
          </button>
        )}
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
