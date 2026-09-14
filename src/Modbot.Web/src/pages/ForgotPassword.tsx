import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type ForgotPasswordWays } from '@/lib/api'
import { Brand, ErrorText, Field, Note, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/**
 * Forgot password (accounts and access design §4.2). The server answers the same sentence
 * whatever happens, and this page says the same sentence back.
 */
export function ForgotPassword({ onBack }: { onBack: () => void }) {
  const [ways, setWays] = useState<ForgotPasswordWays | null>(null)
  const [username, setUsername] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.forgotPasswordWays().then(setWays).catch(() => setWays({ available: false, ways: [], reason: 'Could not reach the Modbot server.' }))
  }, [])

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    api
      .forgotPassword(username)
      .then((r) => setMessage(r.message))
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="grid min-h-screen place-items-center bg-background p-6">
      <div className="w-full max-w-[420px]">
        <Brand />
        <form onSubmit={submit} className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow="Forgot password" title="Get a reset link" />
          <WizardBody>
            {!ways ? (
              <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Checking…</div>
            ) : !ways.available ? (
              <Note tone="warn">
                {ways.reason ?? 'This Modbot cannot send reset links. Ask an administrator.'}
              </Note>
            ) : message ? (
              <Note tone="ok">{message}</Note>
            ) : (
              <>
                <Field label="Username" htmlFor="forgot-username">
                  <Input id="forgot-username" autoComplete="username" autoFocus required value={username} onChange={(e) => setUsername(e.target.value)} />
                </Field>
                <ErrorText>{error}</ErrorText>
              </>
            )}
          </WizardBody>
          <WizardFooter>
            <Button type="button" variant="ghost" onClick={onBack} style={{ height: 'var(--control-h)' }}>
              Back to sign in
            </Button>
            <div className="flex-1" />
            {ways?.available && !message && (
              <Button type="submit" disabled={busy} style={{ height: 'var(--control-h)' }}>
                {busy ? 'Sending…' : 'Send me a link'}
              </Button>
            )}
          </WizardFooter>
        </form>
      </div>
    </div>
  )
}
