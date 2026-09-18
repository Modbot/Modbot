import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type InviteView } from '@/lib/api'
import { openRegisterOnce } from '@/lib/myModbot'
import { Brand, ErrorText, Field, Note, Tickbox, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/**
 * Opening an invite link (accounts and access design §4.1): pick a username and a password,
 * and you are in — as far as the VRChat link page, which comes next for everybody.
 */
export function Join({ token, onJoined }: { token: string; onJoined: () => void }) {
  const [invite, setInvite] = useState<InviteView | null>(null)
  const [username, setUsername] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [updates, setUpdates] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .invite(token)
      .then(setInvite)
      .catch(() =>
        setInvite({
          usable: false,
          reason: 'Could not reach the Modbot server.',
          invitedBy: null,
          roles: [],
          expiresAt: null,
          canSubscribeToUpdates: false,
        }),
      )
  }, [token])

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    if (password !== confirm) {
      setError('The passwords do not match.')
      return
    }
    // Straight from the submit, before anything is awaited, or the browser blocks the tab.
    openRegisterOnce()
    setBusy(true)
    setError(null)
    api
      .acceptInvite(token, {
        username,
        password,
        confirmPassword: confirm,
        email,
        subscribeToUpdates: updates,
      })
      .then(onJoined)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not create the account.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="grid min-h-screen place-items-center bg-background p-6">
      <div className="w-full max-w-[460px]">
        <Brand />
        <form onSubmit={submit} className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow="You're invited" title="Create your Modbot account">
            {invite?.usable
              ? `${invite.invitedBy ?? 'Somebody'} invited you${invite.roles.length ? ` as ${invite.roles.join(', ')}` : ''}.`
              : 'Checking the invite…'}
          </WizardHeader>
          <WizardBody>
            {invite && !invite.usable ? (
              <Note tone="warn">{invite.reason}</Note>
            ) : (
              <>
                <Field label="Username" htmlFor="join-username">
                  <Input id="join-username" autoComplete="username" autoFocus required value={username} onChange={(e) => setUsername(e.target.value)} />
                </Field>
                <Field label="Email" htmlFor="join-email">
                  <Input id="join-email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
                </Field>
                <Field label="Password" hint="at least 12 characters" htmlFor="join-password">
                  <Input id="join-password" type="password" autoComplete="new-password" required minLength={12} value={password} onChange={(e) => setPassword(e.target.value)} />
                </Field>
                <Field label="Confirm password" htmlFor="join-confirm">
                  <Input id="join-confirm" type="password" autoComplete="new-password" required value={confirm} onChange={(e) => setConfirm(e.target.value)} />
                </Field>
                {invite?.canSubscribeToUpdates && (
                  <Tickbox id="join-updates" checked={updates} onChange={setUpdates}>
                    Receive emails from Modbot about new features and updates
                  </Tickbox>
                )}
                <ErrorText>{error}</ErrorText>
              </>
            )}
          </WizardBody>
          <WizardFooter>
            <div className="flex-1" />
            {invite?.usable && (
              <Button type="submit" disabled={busy} style={{ height: 'var(--control-h)' }}>
                {busy ? 'Creating…' : 'Create account'}
              </Button>
            )}
            {invite && !invite.usable && (
              <Button type="button" variant="outline" onClick={() => window.location.assign('/')} style={{ height: 'var(--control-h)' }}>
                Go to sign in
              </Button>
            )}
          </WizardFooter>
        </form>
      </div>
    </div>
  )
}
