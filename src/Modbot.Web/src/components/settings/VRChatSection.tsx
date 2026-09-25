import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { DiagnosisNote } from '@/pages/setup/DiagnosisNote'
import { api, ApiError, type ConnectionDiagnosis, type OnboardingStatus } from '@/lib/api'
import { refreshGateHealth } from '@/lib/useGateHealth'
import { Fact, Field, Hint, Outcome, PasswordField, Placeholder, Switch } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'

/**
 * The VRChat account and the egress proxy — spec 7.1 steps 2 and 3, re-run.
 *
 * Both post to the same endpoints the wizard uses. The proxy check in particular is the reason
 * those steps were built re-runnable: a host that was reachable in March and is Cloudflare-blocked
 * today is exactly the case spec 7.1.1 anticipated, and it must be fixable without re-running
 * setup.
 */
export function VRChatSection({
  status,
  refresh,
}: {
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  return (
    <SettingsSection id="vrchat" title="VRChat Service Account">
      {/* The cards mount only once the status is in hand, so their fields can be initialised
          from it directly instead of being written into by an effect one render later -- which
          is the version that flickers and, worse, clobbers whatever was typed in between. */}
      {status ? (
        <>
          <AccountCard status={status} />
          <CredentialsCard status={status} refresh={refresh} />
          <ProxyCard status={status} refresh={refresh} />
        </>
      ) : (
        <Placeholder>Loading…</Placeholder>
      )}
    </SettingsSection>
  )
}

function AccountCard({ status }: { status: OnboardingStatus }) {
  return (
    <SettingsCard span={12} title="Service Account">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Fact label="Username" value={status.vrChat.username ?? 'Not configured'} />
        <Fact label="Display name" value={status.vrChat.displayName ?? 'Unknown'} />
        <Fact
          label="Last accepted by VRChat"
          value={
            status.vrChat.verifiedAt ? new Date(status.vrChat.verifiedAt).toLocaleString() : 'Never'
          }
          mono={!!status.vrChat.verifiedAt}
        />
        <Fact
          label="Last signed in"
          value={
            status.vrChat.lastSignedInAt ? new Date(status.vrChat.lastSignedInAt).toLocaleString() : 'Never'
          }
          mono={!!status.vrChat.lastSignedInAt}
        />
        <Fact label="Managed group" value={status.group ? status.group.name : 'None chosen'} />
      </div>
    </SettingsCard>
  )
}

function CredentialsCard({
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
  const [diagnosis, setDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [verified, setVerified] = useState<string | null>(null)

  const reverify = (event: React.FormEvent) => {
    event.preventDefault()
    setVerifying(true)
    setDiagnosis(null)
    setError(null)
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
        if (e instanceof ApiError && e.diagnosis) {
          setDiagnosis(e.diagnosis)
          if (e.diagnosis.outcome === 'SignInWaiting') void refreshGateHealth()
        } else setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
      })
      .finally(() => setVerifying(false))
  }

  return (
    <SettingsCard
      title="Update Service Account"
      footer={
        <>
          <Button
            type="submit"
            form="vrchat-credentials"
            size="xs"
            disabled={verifying || !username || !password}
          >
            {verifying ? 'Checking with VRChat…' : 'Verify and store'}
          </Button>
          <Outcome tone="problem">{error}</Outcome>
          <Outcome tone="ok">
            {verified && `VRChat accepted these credentials as ${verified}.`}
          </Outcome>
        </>
      }
    >
      <form id="vrchat-credentials" onSubmit={reverify} className="flex flex-col gap-3">
        <div className="flex max-w-lg flex-col gap-3">
          <Field label="Email or username" value={username} onChange={setUsername} placeholder="" />
          <PasswordField label="Password" value={password} onChange={setPassword} />
          <PasswordField label="TOTP secret (optional)" value={totpSecret} onChange={setTotpSecret} />
        </div>

        {diagnosis && <DiagnosisNote diagnosis={diagnosis} />}
      </form>
    </SettingsCard>
  )
}

function ProxyCard({
  status,
  refresh,
}: {
  status: OnboardingStatus
  refresh: () => Promise<void>
}) {
  const [useProxy, setUseProxy] = useState(status.connection.proxyUrl !== null)
  const [proxyUrl, setProxyUrl] = useState(status.connection.proxyUrl ?? '')
  const [proxyUsername, setProxyUsername] = useState(status.connection.proxyUsername ?? '')
  const [proxyPassword, setProxyPassword] = useState('')
  const [testing, setTesting] = useState(false)
  const [diagnosis, setDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [error, setError] = useState<string | null>(null)

  const test = () => {
    setTesting(true)
    setDiagnosis(null)
    setError(null)

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
        setDiagnosis(result)
        if (result.proxyWouldHelp) setUseProxy(true)
        setProxyPassword('')
        await refresh()
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'),
      )
      .finally(() => setTesting(false))
  }

  return (
    <SettingsCard
      title="Egress proxy"
      footer={
        <>
          <Button size="xs" variant="outline" disabled={testing} onClick={test}>
            {testing ? 'Testing…' : 'Test connection'}
          </Button>
          <Hint>
            {status.connection.checkedAt
              ? `Last passed ${new Date(status.connection.checkedAt).toLocaleString()}`
              : 'Never passed'}
          </Hint>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <Switch checked={useProxy} onChange={setUseProxy}>
        Enable egress SOCKS5 proxy
      </Switch>

      {useProxy && (
        <div className="flex max-w-lg flex-col gap-3">
          <Field
            label="Proxy URL"
            value={proxyUrl}
            onChange={setProxyUrl}
            placeholder="socks5://host:port"
          />
          <Field label="Username" value={proxyUsername} onChange={setProxyUsername} placeholder="" />
          <PasswordField
            label={status.connection.proxyPasswordStored ? 'Password (stored)' : 'Password'}
            value={proxyPassword}
            onChange={setProxyPassword}
          />
        </div>
      )}

      {diagnosis && <DiagnosisNote diagnosis={diagnosis} />}
    </SettingsCard>
  )
}
