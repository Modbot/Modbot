import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type ResetView } from '@/lib/api'
import { Brand, ErrorText, Field, Note, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/** Opening a reset link (accounts and access design §4.1): set a new password, then sign in. */
export function ResetPassword({ token }: { token: string }) {
  const [view, setView] = useState<ResetView | null>(null)
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .resetLink(token)
      .then(setView)
      .catch(() => setView({ usable: false, reason: 'Could not reach the Modbot server.', username: null }))
  }, [token])

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    if (password !== confirm) {
      setError('The passwords do not match.')
      return
    }
    setBusy(true)
    setError(null)
    api
      .useResetLink(token, { password, confirmPassword: confirm })
      .then(() => setDone(true))
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not set the password.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="w-full max-w-[440px]">
        <Brand />
        <form onSubmit={submit} className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow="Reset link" title={done ? 'Password changed' : 'Choose a new password'}>
            {view?.usable && !done ? `For the account ${view.username}.` : undefined}
          </WizardHeader>
          <WizardBody>
            {done ? (
              <Note tone="ok">Every session was signed out.</Note>
            ) : view && !view.usable ? (
              <Note tone="warn">{view.reason}</Note>
            ) : view ? (
              <>
                <Field label="New password" hint="at least 12 characters" htmlFor="reset-password">
                  <Input id="reset-password" type="password" autoComplete="new-password" autoFocus required minLength={12} value={password} onChange={(e) => setPassword(e.target.value)} />
                </Field>
                <Field label="Confirm password" htmlFor="reset-confirm">
                  <Input id="reset-confirm" type="password" autoComplete="new-password" required value={confirm} onChange={(e) => setConfirm(e.target.value)} />
                </Field>
                <ErrorText>{error}</ErrorText>
              </>
            ) : (
              <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Checking the link…</div>
            )}
          </WizardBody>
          <WizardFooter>
            <div className="flex-1" />
            {view?.usable && !done ? (
              <Button type="submit" disabled={busy} style={{ height: 'var(--control-h)' }}>
                {busy ? 'Saving…' : 'Set password'}
              </Button>
            ) : (
              <Button type="button" onClick={() => window.location.assign('/')} style={{ height: 'var(--control-h)' }}>
                Go to sign in
              </Button>
            )}
          </WizardFooter>
        </form>
      </div>
    </div>
  )
}
