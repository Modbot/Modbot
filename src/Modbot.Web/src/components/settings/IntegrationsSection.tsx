import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type EmailSettings, type OnboardingStatus, type TestEmailResult } from '@/lib/api'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { Checkbox, Fact, Field, Outcome, PasswordField, Placeholder } from './fields'
import { PublicRoomsCard } from './PublicRoomsCard'
import { SettingsCard, SettingsSection } from './SettingsCard'

/**
 * Email — spec 7.1 step 5, re-run. The public address moved to Host & Database.
 * Discord has its own tab since event
 * channels arrived (Discord event routes design §7).
 *
 * SMTP is not validated by connecting, here or in the wizard: sending mail is slow and fails for
 * reasons unrelated to the value being wrong, and is not worth blocking a settings save on.
 *
 * The daily email limit and its queue sit on the same card (accounts and access design §4.4):
 * labels and numbers only, the rules live in the spec.
 */
export function IntegrationsSection({
  status,
  refresh,
}: {
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  return (
    <SettingsSection id="integrations" title="Integrations">
      {/* Mounted only once the status is in hand -- see VRChatSection for why. */}
      {status ? (
        <IntegrationsForm status={status} refresh={refresh} />
      ) : (
        <Placeholder>Loading…</Placeholder>
      )}
      <PublicRoomsCard />
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
  const [host, setHost] = useState(status.integrations.smtpHost ?? '')
  const [port, setPort] = useState('')
  const [smtpUsername, setSmtpUsername] = useState('')
  const [smtpPassword, setSmtpPassword] = useState('')
  const [fromAddress, setFromAddress] = useState('')
  const [useTls, setUseTls] = useState(true)

  const [testTo, setTestTo] = useState('')
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<TestEmailResult | null>(null)

  const [email, setEmail] = useState<EmailSettings | null>(null)
  const [limit, setLimit] = useState('')

  const loadEmail = useCallback(
    () =>
      api
        .emailSettings()
        .then((next) => {
          setEmail(next)
          setLimit(String(next.limitPer24Hours))
        })
        .catch(() => setEmail(null)),
    [],
  )

  useEffect(() => {
    void loadEmail()
  }, [loadEmail])

  // Proves the *saved* settings work. Save first, then test: a wrong relay setting should be
  // found here, not by a moderator who cannot get back in.
  const sendTest = () => {
    setTesting(true)
    setTestResult(null)
    api
      .sendTestEmail(testTo)
      .then((result) => {
        setTestResult(result)
        if (result.queued) void loadEmail()
      })
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

    const limitChanged = email !== null && limit.trim() !== String(email.limitPer24Hours)

    const saveLimit = limitChanged
      ? api.setEmailLimit(Number(limit.trim())).then((next) => {
          setEmail(next)
          setLimit(String(next.limitPer24Hours))
        })
      : Promise.resolve()

    saveLimit
      .then(() => api.saveIntegrations({
        smtp: {
          host,
          ...(port ? { port: Number(port) } : {}),
          username: smtpUsername,
          ...(smtpPassword ? { password: smtpPassword } : {}),
          fromAddress,
          useTls,
        },
      }))
      .then(async () => {
        setSaved(true)
        setSmtpPassword('')
        await refresh()
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not save.'),
      )
      .finally(() => setSaving(false))
  }

  // One form around the card and its Save row. `contents` keeps the
  // form element out of the layout so the cards stay direct children of the grid. A blank password
  // field is left out, so opening this page and pressing Save never clears a stored one.
  return (
    <form onSubmit={save} className="contents">
      <SettingsCard title="Email (SMTP)">
        <Fact
          label="Relay"
          value={
            status.integrations.smtpConfigured
              ? (status.integrations.smtpHost ?? 'Configured')
              : 'Not configured'
          }
        />
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
          <Outcome tone="ok">{testResult?.sent && 'Sent.'}</Outcome>
          <Outcome tone="ok">{testResult?.queued && queuedText(testResult.sendsAt ?? null)}</Outcome>
          <Outcome tone="problem">{testResult && !testResult.sent && !testResult.queued ? testResult.error : null}</Outcome>
        </div>

        <div className="mt-4 grid gap-3 sm:grid-cols-4">
          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Email limit per 24 hours</span>
            <Input
              type="number"
              inputMode="numeric"
              min={email?.minimumLimit ?? 20}
              step={1}
              value={limit}
              disabled={!email}
              onChange={(e) => setLimit(e.target.value)}
            />
          </label>
          {email && (
            <>
              <Fact label="Sent in the last 24 hours" value={`${email.sentInLast24Hours} of ${email.limitPer24Hours}`} />
              <Fact label="Queued" value={String(email.queued)} />
              <Fact label="Next queued email" value={email.nextSendAt ? when(email.nextSendAt) : '—'} />
            </>
          )}
        </div>

        {email && email.emails.length > 0 && <EmailQueueTable email={email} />}
      </SettingsCard>

      <div className="col-span-12 flex flex-wrap items-center gap-3">
        <Button type="submit" size="sm" disabled={saving}>
          {saving ? 'Saving…' : 'Save integrations'}
        </Button>
        <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
        <Outcome tone="problem">{error}</Outcome>
      </div>
    </form>
  )
}

const cellClass = 'py-1 pr-3 align-top'
const headClass = 'py-1 pr-3 font-normal'

const when = (iso: string) => new Date(iso).toLocaleString()

const queuedText = (sendsAt: string | null) => (sendsAt ? `Queued, sends at ${when(sendsAt)}.` : 'Queued.')

const STATE_LABEL: Record<string, string> = {
  queued: 'Queued',
  sending: 'Sending',
  failed: 'Failed',
  expired: 'Expired',
}

function EmailQueueTable({ email }: { email: EmailSettings }) {
  return (
    <div className="mt-4 overflow-x-auto">
      <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="text-left text-muted-foreground">
          <tr>
            <th className={headClass}>Recipient</th>
            <th className={headClass}>Kind</th>
            <th className={headClass}>Queued at</th>
            <th className={headClass}>State</th>
          </tr>
        </thead>
        <tbody>
          {email.emails.map((row) => (
            <tr key={row.id}>
              <td className={cn(cellClass, 'max-w-[16rem] truncate')} title={row.to}>
                {row.to}
              </td>
              <td className={cn(cellClass, 'whitespace-nowrap')}>{row.kind === 'account' ? 'Account' : 'Other'}</td>
              <td className={cn(cellClass, 'whitespace-nowrap tabular-nums')}>{when(row.queuedAt)}</td>
              <td className={cn(cellClass, row.state === 'failed' && 'text-destructive')}>
                {STATE_LABEL[row.state] ?? row.state}
                {row.state === 'failed' && row.error ? `: ${row.error}` : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
