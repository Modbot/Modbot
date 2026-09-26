import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type EmailSettings, type OnboardingStatus, type TestEmailResult } from '@/lib/api'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { Checkbox, Fact, Field, Outcome, PasswordField, Placeholder } from './fields'
import { HealthAlertsCard } from './HealthAlertsCard'
import { PublicInstancesCard } from './PublicInstancesCard'
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
  statusError,
  refresh,
}: {
  status: OnboardingStatus | null
  /** Why `status` could not be read, while it is null because the read failed. */
  statusError?: string | null
  refresh: () => Promise<void>
}) {
  return (
    <SettingsSection id="integrations" title="Integrations">
      {/* Mounted only once the status is in hand -- see VRChatSection for why. */}
      {status ? (
        <IntegrationsForm status={status} refresh={refresh} />
      ) : (
        <Placeholder tone={statusError ? 'danger' : undefined}>{statusError ?? 'Loading…'}</Placeholder>
      )}
      {/* Beside the email card, or on a row of its own while that card waits for the status. */}
      <PublicInstancesCard span={status ? 6 : 12} />
      <HealthAlertsCard />
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

  // The form sits inside the card, so the card stays a direct child of the grid, and the Save on
  // the footer reaches it by id. `contents` keeps the form out of the card's layout. A blank password
  // field is left out, so opening this page and pressing Save never clears a stored one. The card is
  // flush so the queue table meets its edges; the fields above it keep the card's inset in a block of
  // their own.
  return (
    <SettingsCard
      title="Email (SMTP)"
      flush
      footer={
        <>
          <Button type="submit" form="integrations-form" size="xs" disabled={saving}>
            {saving ? 'Saving…' : 'Save integrations'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <form id="integrations-form" onSubmit={save} className="contents">
        <div className="flex flex-col gap-3 p-(--panel-pad)">
          <Fact
            label="Relay"
            value={
              status.integrations.smtpConfigured
                ? (status.integrations.smtpHost ?? 'Configured')
                : 'Not configured'
            }
            mono={status.integrations.smtpConfigured && !!status.integrations.smtpHost}
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
          <div className="flex flex-wrap items-end gap-3">
            <div className="min-w-[16rem]">
              <Field label="Send a test message to" value={testTo} onChange={setTestTo} placeholder="you@example.com" />
            </div>
            <Button
              type="button"
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

          <div className="grid items-end gap-3 sm:grid-cols-4">
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
                <Fact label="Sent in the last 24 hours" value={`${email.sentInLast24Hours} of ${email.limitPer24Hours}`} mono />
                <Fact label="Queued" value={String(email.queued)} mono />
                <Fact label="Next queued email" value={email.nextSendAt ? when(email.nextSendAt) : '—'} mono={!!email.nextSendAt} />
              </>
            )}
          </div>
        </div>

        {email && email.emails.length > 0 && <EmailQueueTable email={email} />}
      </form>
    </SettingsCard>
  )
}

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
    // Under the fields, with a hairline between them and the column names.
    <div className="border-t border-t-(length:--hairline)">
      <Table
        head={
          <>
            <Th>Recipient</Th>
            <Th>Kind</Th>
            <Th>Queued at</Th>
            <Th>State</Th>
          </>
        }
      >
        {email.emails.map((row) => (
          <Tr key={row.id}>
            <Td className="max-w-[16rem] truncate" title={row.to}>
              {row.to}
            </Td>
            <Td>{row.kind === 'account' ? 'Account' : 'Other'}</Td>
            <Td className="font-mono">{when(row.queuedAt)}</Td>
            <Td
              className={cn(
                row.state === 'failed' && 'text-destructive',
                row.state === 'failed' && row.error && 'min-w-[16rem] whitespace-normal',
              )}
            >
              {STATE_LABEL[row.state] ?? row.state}
              {row.state === 'failed' && row.error ? `: ${row.error}` : null}
            </Td>
          </Tr>
        ))}
      </Table>
    </div>
  )
}
