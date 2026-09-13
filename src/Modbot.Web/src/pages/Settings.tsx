import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { DiagnosisNote } from '@/pages/setup/DiagnosisNote'
import {
  api,
  ApiError,
  type ConnectionDiagnosis,
  type DataSettings,
  type OnboardingStatus,
  type SyncSettings,
} from '@/lib/api'
import { cn } from '@/lib/utils'

/** Binary, because that is how disks are sized and how most providers bill. */
const GB = 1024 * 1024 * 1024

function bytes(n: number): string {
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = n / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`
}

function seconds(n: number): string {
  if (n < 60) return `${n % 1 === 0 ? n : n.toFixed(1)}s`
  if (n < 3600) return `${(n / 60).toFixed(n % 60 === 0 ? 0 : 1)} min`
  return `${(n / 3600).toFixed(1)} h`
}

/**
 * The what-if inputs live in the browser because the server does not store them — nothing in
 * Modbot behaves differently for having been told a per-GB price. Remembering them here means
 * they survive a reload without a column and a migration existing to hold a number on a screen.
 */
function remembered(key: string): string {
  try {
    return localStorage.getItem(key) ?? ''
  } catch {
    return ''
  }
}

function remember(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* private windows and blocked site data are fine; the field just does not persist */
  }
}

const TABS = [
  { id: 'data', label: 'Data' },
  { id: 'vrchat', label: 'VRChat account' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'sync', label: 'Sync' },
] as const

type TabId = (typeof TABS)[number]['id']

/**
 * Settings.
 *
 * Three of these four tabs are the onboarding wizard's own steps, re-run in place. That is not a
 * convenience — spec 7.1 designed each step as an independently re-runnable slice for exactly this
 * reason, so a deployment whose host became WAF-blocked in March re-runs the connection check
 * rather than the wizard. Duplicating the logic here would give Modbot two implementations of
 * "store a VRChat credential", and the second one would be the one that drifts.
 */
export function Settings() {
  const [tab, setTab] = useState<TabId>('data')
  const [status, setStatus] = useState<OnboardingStatus | null>(null)

  const refresh = useCallback(
    () => api.onboardingStatus().then(setStatus).catch(() => setStatus(null)),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" className="flex flex-wrap gap-1 border-b pb-2" style={{ borderBottomWidth: 'var(--hairline)' }}>
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            aria-selected={tab === t.id}
            onClick={() => setTab(t.id)}
            className={cn(
              'rounded-md px-3 font-medium transition-colors',
              tab === t.id
                ? 'bg-accent text-accent-foreground'
                : 'text-muted-foreground hover:bg-secondary hover:text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)', height: 'var(--control-h)' }}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'data' && <DataTab />}

      {/* The two wizard-backed tabs mount only once the status is in hand, so their fields can be
          initialised from it directly instead of being written into by an effect one render
          later -- which is the version that flickers and, worse, clobbers whatever was typed in
          between. */}
      {tab === 'vrchat' &&
        (status ? <VRChatTab status={status} refresh={refresh} /> : <Note>Loading…</Note>)}
      {tab === 'integrations' &&
        (status ? <IntegrationsTab status={status} refresh={refresh} /> : <Note>Loading…</Note>)}

      {tab === 'sync' && <SyncTab />}
    </div>
  )
}

function DataTab() {
  const [data, setData] = useState<DataSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [cost, setCost] = useState(() => remembered('modbot.costPerGbMonth'))
  const [capacity, setCapacity] = useState(() => remembered('modbot.capacityGb'))

  const load = useCallback(() => {
    const budget = {
      costPerGbMonth: Number(cost) || undefined,
      capacityBytes: Number(capacity) ? Number(capacity) * GB : undefined,
    }
    api
      .dataSettings(budget)
      .then((next) => {
        setData(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not load settings.'),
      )
  }, [cost, capacity])

  useEffect(() => load(), [load])

  if (error) return <Note>{error}</Note>
  if (!data) return <Note>Loading…</Note>

  const { storage, deployment, retention } = data
  const keepingEverything =
    retention.moderationFactRetentionDays === 0 && retention.presenceFactRetentionDays === 0

  return (
    <div className="flex flex-col gap-4">
      <Section title="Deployment">
        <Row label="Version" value={deployment.version} />
        <Row
          label="Host"
          value={
            deployment.platform +
            (deployment.platformEvidence ? ` (detected from ${deployment.platformEvidence})` : '')
          }
        />
        <Row
          label="Log files"
          value={deployment.logFilesWritten ? 'Written to disk' : 'Console and Seq only'}
        />
        <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {deployment.persistenceExplanation}
        </p>
      </Section>

      <Section title="Storage">
        <Row label="Database" value={bytes(storage.bytes)} />
        <Row label="Facts recorded" value={storage.facts.toLocaleString()} />
        {storage.facts > 0 && (
          <Row label="Per fact" value={`${Math.round(storage.bytesPerFact)} bytes, indexes included`} />
        )}
        <Row
          label="Arriving"
          value={
            storage.observedDays >= 1
              ? `${Math.round(storage.factsPerDay).toLocaleString()} facts/day, measured over ${Math.round(storage.observedDays)} days`
              : 'Not enough history to measure yet'
          }
        />

        <div className="mt-4 grid grid-cols-2 gap-3">
          <Field
            label="Cost per GB / month"
            placeholder="0.25"
            value={cost}
            onChange={(v) => {
              setCost(v)
              remember('modbot.costPerGbMonth', v)
            }}
          />
          <Field
            label="Disk size (GB)"
            placeholder="500"
            value={capacity}
            onChange={(v) => {
              setCapacity(v)
              remember('modbot.capacityGb', v)
            }}
          />
        </div>

        {storage.confidence === 'Insufficient' ? (
          <p className="mt-4 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            No projection yet. Modbot will not extrapolate from less than a day of history — the
            answer would be wrong by an order of magnitude in whichever direction today happened
            to go.
          </p>
        ) : (
          <>
            <table className="mt-4 w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="text-muted-foreground">
                <tr>
                  <th className="text-left font-normal">If this rate continues</th>
                  <th className="text-right font-normal">Size</th>
                  <th className="text-right font-normal">Cost / month</th>
                </tr>
              </thead>
              <tbody>
                {storage.horizons.map((h) => (
                  <tr key={h.months}>
                    <td className="py-1">In {h.months} months</td>
                    <td className="py-1 text-right tabular-nums">{bytes(h.projectedBytes)}</td>
                    <td className="py-1 text-right tabular-nums">
                      {h.monthlyCost === null ? '—' : `$${h.monthlyCost.toFixed(2)}`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              A straight line, which real growth is not — a group that opens more instances
              generates more facts per member. Treat it as an order of magnitude.
              {storage.confidence === 'Low' &&
                ' Based on under a month of history, so a single busy weekend still moves it a lot.'}
            </p>

            {storage.capacityExhausted && (
              <p className="mt-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                At this rate that disk fills around{' '}
                {new Date(storage.capacityExhausted).toLocaleDateString()}.
              </p>
            )}
          </>
        )}
      </Section>

      <Section title="Retention">
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {keepingEverything
            ? 'Modbot is keeping everything, which is the default. History cannot be backfilled: whatever is deleted is gone, and no amount of API access brings it back.'
            : 'A retention window is set. Facts past it are destroyed permanently.'}
        </p>
        <RetentionForm current={retention} onSaved={load} />
      </Section>
    </div>
  )
}

/**
 * The VRChat account and the egress proxy — spec 7.1 steps 2 and 3, re-run.
 *
 * Both post to the same endpoints the wizard uses. The proxy check in particular is the reason
 * those steps were built re-runnable: a host that was reachable in March and is Cloudflare-blocked
 * today is exactly the case spec 7.1.1 anticipated, and it must be fixable without re-running
 * setup.
 */
function VRChatTab({
  status,
  refresh,
}: {
  status: OnboardingStatus
  refresh: () => Promise<void>
}) {
  const [username, setUsername] = useState(status.vrChat.username ?? '')
  const [password, setPassword] = useState('')
  const [totpSecret, setTotpSecret] = useState('')
  const [verifying, setVerifying] = useState(false)
  const [verifyDiagnosis, setVerifyDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [verifyError, setVerifyError] = useState<string | null>(null)
  const [verified, setVerified] = useState<string | null>(null)

  const [useProxy, setUseProxy] = useState(status.connection.proxyUrl !== null)
  const [proxyUrl, setProxyUrl] = useState(status.connection.proxyUrl ?? '')
  const [proxyUsername, setProxyUsername] = useState(status.connection.proxyUsername ?? '')
  const [proxyPassword, setProxyPassword] = useState('')
  const [testing, setTesting] = useState(false)
  const [testDiagnosis, setTestDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [testError, setTestError] = useState<string | null>(null)

  const reverify = (event: React.FormEvent) => {
    event.preventDefault()
    setVerifying(true)
    setVerifyDiagnosis(null)
    setVerifyError(null)
    setVerified(null)

    api
      .verifyVRChat({ username, password, totpSecret: totpSecret || null })
      .then(async (result) => {
        setVerified(result.displayName ?? username)
        setPassword('')
        setTotpSecret('')
        await refresh()
      })
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.diagnosis) setVerifyDiagnosis(e.diagnosis)
        else setVerifyError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
      })
      .finally(() => setVerifying(false))
  }

  const test = () => {
    setTesting(true)
    setTestDiagnosis(null)
    setTestError(null)

    api
      .testConnection(
        useProxy
          ? {
              useProxy: true,
              proxyUrl,
              proxyUsername,
              // Omitted rather than sent empty, so re-testing after fixing a typo in the URL keeps
              // the stored password instead of silently clearing it.
              ...(proxyPassword ? { proxyPassword } : {}),
            }
          : { useProxy: false },
      )
      .then(async (result) => {
        setTestDiagnosis(result)
        if (result.proxyWouldHelp) setUseProxy(true)
        setProxyPassword('')
        await refresh()
      })
      .catch((e: unknown) =>
        setTestError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'),
      )
      .finally(() => setTesting(false))
  }

  return (
    <div className="flex flex-col gap-4">
      <Section title="Account">
        <Row label="Username" value={status.vrChat.username ?? 'Not configured'} />
        <Row label="Display name" value={status.vrChat.displayName ?? 'Unknown'} />
        <Row
          label="Last accepted by VRChat"
          value={
            status.vrChat.verifiedAt
              ? new Date(status.vrChat.verifiedAt).toLocaleString()
              : 'Never'
          }
        />
        <Row label="Managed group" value={status.group ? status.group.name : 'None chosen'} />
        <p className="mt-2 max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Modbot acts as this account, and VRChat attributes everything Modbot does to it. Changing
          it changes whose name appears in the group's own audit log from that point on.
        </p>
      </Section>

      <Section title="Re-verify credentials">
        <form onSubmit={reverify} className="flex flex-col gap-3">
          <p className="max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Checked against VRChat before anything is stored. A failure here is shown as a full
            diagnosis rather than "login failed", because the commonest one on a rented host is a
            Cloudflare block — and that would send you to check a password that was never wrong.
          </p>

          <div className="grid gap-3 sm:grid-cols-3">
            <Field label="Email or username" value={username} onChange={setUsername} placeholder="" />
            <PasswordField label="Password" value={password} onChange={setPassword} />
            <PasswordField
              label="TOTP secret (optional)"
              value={totpSecret}
              onChange={setTotpSecret}
            />
          </div>

          {verifyDiagnosis && <DiagnosisNote diagnosis={verifyDiagnosis} />}
          {verifyError && (
            <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {verifyError}
            </p>
          )}
          {verified && (
            <p className="text-ok" style={{ fontSize: 'var(--text-small)' }}>
              VRChat accepted these credentials as {verified}.
            </p>
          )}

          <div>
            <Button type="submit" size="sm" disabled={verifying || !username || !password}>
              {verifying ? 'Checking with VRChat…' : 'Verify and store'}
            </Button>
          </div>
        </form>
      </Section>

      <Section title="Egress proxy">
        <p className="max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Only useful for one failure: Cloudflare blocking this host's network. It fixes nothing
          else, and configuring one on a working install is a way to break it. The check below says
          plainly which failure you have.
        </p>

        <div className="mt-3 flex flex-col gap-3">
          <label className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
            <input
              type="checkbox"
              checked={useProxy}
              onChange={(e) => setUseProxy(e.target.checked)}
            />
            Route VRChat traffic through a proxy
          </label>

          {useProxy && (
            <div className="grid gap-3 sm:grid-cols-3">
              <Field
                label="Proxy URL"
                value={proxyUrl}
                onChange={setProxyUrl}
                placeholder="http://host:port"
              />
              <Field label="Username" value={proxyUsername} onChange={setProxyUsername} placeholder="" />
              <PasswordField
                label={
                  status.connection.proxyPasswordStored
                    ? 'Password (stored — leave blank to keep)'
                    : 'Password'
                }
                value={proxyPassword}
                onChange={setProxyPassword}
              />
            </div>
          )}

          {testDiagnosis && <DiagnosisNote diagnosis={testDiagnosis} />}
          {testError && (
            <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {testError}
            </p>
          )}

          <div className="flex items-center gap-3">
            <Button size="sm" variant="outline" disabled={testing} onClick={test}>
              {testing ? 'Testing…' : 'Test connection'}
            </Button>
            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {status.connection.checkedAt
                ? `Last passed ${new Date(status.connection.checkedAt).toLocaleString()}`
                : 'Never passed'}
            </span>
          </div>
        </div>
      </Section>
    </div>
  )
}

/**
 * Discord and SMTP — spec 7.1 step 5, re-run.
 *
 * Neither is validated by connecting, here or in the wizard. A Discord token is checked by
 * starting a gateway session and SMTP by sending mail; both are slow, both fail for reasons
 * unrelated to the value being wrong, and neither is worth blocking a settings save on.
 */
function IntegrationsTab({
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

/**
 * Sync cadence — read-only, and saying so.
 *
 * Spec 4.2.1 calls for a slider per rate with the cap enforced server-side on write. There is no
 * slider because there is nowhere to write to: the intervals are process configuration fixed at
 * start-up, and no settings column holds them. A control that discarded what the operator typed
 * would be worse than none — they would believe they had dialled a rate down when they had not.
 */
function SyncTab() {
  const [settings, setSettings] = useState<SyncSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .syncSettings()
      .then(setSettings)
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not load sync settings.'),
      )
  }, [])

  if (error) return <Note>{error}</Note>
  if (!settings) return <Note>Loading…</Note>

  return (
    <div className="flex flex-col gap-4">
      <div
        className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3"
        style={{ borderWidth: 'var(--hairline)' }}
      >
        <div className="font-medium">These cannot be changed here yet.</div>
        <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {settings.editableExplanation}
        </p>
      </div>

      {!settings.running && (
        <Note>
          The producers are not running in this process, so the values below are what would be used
          rather than what is.
        </Note>
      )}

      <Section title="Group audit log">
        <p className="mb-3 max-w-2xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Adaptive: fast while entries are arriving, geometrically slower while they are not. The
          interval in force right now, and the producer's reason for it, are on the Sync health
          screen — they change every poll and are a diagnostic rather than a setting.
        </p>
        <Row label="Fastest interval" value={seconds(settings.auditLog.minIntervalSeconds)} />
        <Row label="Slowest interval" value={seconds(settings.auditLog.maxIntervalSeconds)} />
        <Row
          label="Pacing floor"
          value={`${seconds(settings.auditLog.pacingFloorSeconds)} — configuration may only ever make this slower`}
        />
        <Row label="Back-off per quiet poll" value={`${settings.auditLog.quietBackoff}×`} />
        <Row label="Jitter" value={`up to +${Math.round(settings.auditLog.jitterFraction * 100)}%`} />
        <Row label="Entries per request" value={String(settings.auditLog.pageSize)} />
        <Row label="Requests per catch-up pass" value={String(settings.auditLog.maxPagesPerRun)} />
        <Row
          label="Re-read window"
          value={`${seconds(settings.auditLog.overlapSeconds)} behind the cursor, so a late entry is not missed`}
        />
        <Row
          label="Backfill"
          value={
            settings.auditLog.backfill
              ? `On, up to ${settings.auditLog.maxBackfillPages.toLocaleString()} pages`
              : 'Off — only entries from now on are recorded'
          }
        />
      </Section>

      <Section title="Group info">
        <Row label="Interval" value={seconds(settings.groupInfo.intervalSeconds)} />
        <Row label="After a failure" value={seconds(settings.groupInfo.retryIntervalSeconds)} />
        <Row
          label="While rate limited"
          value={seconds(settings.groupInfo.rateLimitedIntervalSeconds)}
        />
        <Row label="Pacing floor" value={seconds(settings.groupInfo.pacingFloorSeconds)} />
        <Row label="Jitter" value={`up to +${Math.round(settings.groupInfo.jitterFraction * 100)}%`} />
      </Section>
    </div>
  )
}

function RetentionForm({
  current,
  onSaved,
}: {
  current: DataSettings['retention']
  onSaved: () => void
}) {
  const [moderation, setModeration] = useState(String(current.moderationFactRetentionDays))
  const [presence, setPresence] = useState(String(current.presenceFactRetentionDays))
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const dirty =
    moderation !== String(current.moderationFactRetentionDays) ||
    presence !== String(current.presenceFactRetentionDays)

  const save = () => {
    setSaving(true)
    setProblem(null)
    api
      .setRetention({
        moderationFactRetentionDays: Number(moderation) || 0,
        presenceFactRetentionDays: Number(presence) || 0,
      })
      .then(onSaved)
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Could not save.'),
      )
      .finally(() => setSaving(false))
  }

  return (
    <div className="mt-3">
      <div className="grid grid-cols-2 gap-3">
        <Field
          label="Moderation facts (days)"
          placeholder="0"
          value={moderation}
          onChange={setModeration}
        />
        <Field
          label="Presence facts (days)"
          placeholder="0"
          value={presence}
          onChange={setPresence}
        />
      </div>
      <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        0 keeps forever. Rollups are never aged out, so charts keep their full history even where
        the underlying facts have been removed.
      </p>
      {problem && (
        <p className="mt-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {problem}
        </p>
      )}
      <Button className="mt-3" size="sm" disabled={!dirty || saving} onClick={save}>
        {saving ? 'Saving…' : 'Save retention'}
      </Button>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="mb-3 font-medium">{title}</div>
        {children}
      </CardContent>
    </Card>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-4 py-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right tabular-nums">{value}</span>
    </div>
  )
}

function Field({
  label,
  value,
  placeholder,
  onChange,
}: {
  label: string
  value: string
  placeholder: string
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

function PasswordField({
  label,
  value,
  onChange,
}: {
  label: string
  value: string
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        type="password"
        autoComplete="new-password"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

function Note({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
