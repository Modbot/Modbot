import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { DiagnosisNote } from '@/pages/setup/DiagnosisNote'
import { api, ApiError, type ConnectionDiagnosis, type OnboardingStatus } from '@/lib/api'
import { Field, PasswordField, Row, Section } from './fields'

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
