import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { VRChatLinkPanel } from '@/components/VRChatLinkPanel'
import { ApiError, api, type CurrentUser } from '@/lib/api'
import { ErrorText, Field, Note } from '@/pages/setup/WizardChrome'

/**
 * The signed-in person's own account (accounts and access design §4, §8): username, password,
 * where a reset link can reach them, the linked VRChat account, and the way out of every session.
 */
export function Account({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card>
        <CardContent className="space-y-3">
          <div className="font-semibold">You</div>
          <div style={{ fontSize: 'var(--text-small)' }}>
            <div>
              Signed in as <span className="font-medium">{me.username}</span>
            </div>
            <div className="mt-1 flex flex-wrap gap-1">
              {me.roles.map((r) => (
                <Badge key={r} variant="secondary">
                  {r}
                </Badge>
              ))}
              {me.roles.length === 0 && <span className="text-muted-foreground">No roles yet</span>}
            </div>
          </div>
        </CardContent>
      </Card>

      <ChangeUsername me={me} onChanged={onChanged} />
      <ChangePassword />
      <Contact me={me} onChanged={onChanged} />

      <Card>
        <CardContent className="space-y-3">
          <div className="font-semibold">Your VRChat account</div>
          <div style={{ fontSize: 'var(--text-small)' }}>
            {me.vrChatLinked ? (
              <>
                Linked to <span className="font-medium">{me.vrChatDisplayName ?? me.vrChatUserId}</span>{' '}
                <span className="font-mono text-muted-foreground">{me.vrChatUserId}</span>
              </>
            ) : (
              'Not linked yet.'
            )}
          </div>
          <details>
            <summary className="cursor-pointer text-primary" style={{ fontSize: 'var(--text-small)' }}>
              Link a different account
            </summary>
            <div className="mt-3">
              <VRChatLinkPanel compact onLinked={onChanged} />
            </div>
          </details>
        </CardContent>
      </Card>

      <SignOutEverywhere />
    </div>
  )
}

function ChangeUsername({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [username, setUsername] = useState(me.username)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .changeUsername({ username, currentPassword: password })
      .then(() => {
        setDone(true)
        setPassword('')
        onChanged()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not change your username.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardContent>
        <form onSubmit={submit} className="space-y-3">
          <div className="font-semibold">Change your username</div>
          <Field label="New username" htmlFor="acct-username">
            <Input id="acct-username" required autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} />
          </Field>
          <Field label="Your current password" htmlFor="acct-username-pw">
            <Input id="acct-username-pw" type="password" required autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          <ErrorText>{error}</ErrorText>
          <div className="flex items-center gap-3">
            <Button type="submit" size="sm" disabled={busy || username.trim() === me.username || !password}>
              {busy ? 'Saving…' : 'Change username'}
            </Button>
            {done && <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>Changed.</span>}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

function ChangePassword() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    if (next !== confirm) {
      setError('The passwords do not match.')
      return
    }
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .changePassword({ currentPassword: current, newPassword: next, confirmPassword: confirm })
      .then(() => {
        setDone(true)
        setCurrent('')
        setNext('')
        setConfirm('')
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not change your password.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardContent>
        <form onSubmit={submit} className="space-y-3">
          <div className="font-semibold">Change your password</div>
          <Field label="Current password" htmlFor="acct-pw-current">
            <Input id="acct-pw-current" type="password" required autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="New password" hint="at least 12 characters" htmlFor="acct-pw-new">
              <Input id="acct-pw-new" type="password" required minLength={12} autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
            </Field>
            <Field label="Confirm" htmlFor="acct-pw-confirm">
              <Input id="acct-pw-confirm" type="password" required autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            </Field>
          </div>
          <ErrorText>{error}</ErrorText>
          <div className="flex items-center gap-3">
            <Button type="submit" size="sm" disabled={busy}>
              {busy ? 'Saving…' : 'Change password'}
            </Button>
            {done && (
              <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>
                Changed. Other sessions signed out.
              </span>
            )}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

function Contact({ me, onChanged }: { me: CurrentUser; onChanged: () => void }) {
  const [email, setEmail] = useState(me.email ?? '')
  const [discord, setDiscord] = useState(me.discordUserId ?? '')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setDone(false)
    setError(null)
    api
      .setOwnContact({ email, discordUserId: discord })
      .then(() => {
        setDone(true)
        onChanged()
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardContent>
        <form onSubmit={submit} className="space-y-3">
          <div className="font-semibold">Password reset</div>
          <Field label="Email" htmlFor="acct-email">
            <Input id="acct-email" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <Field label="Discord user id" htmlFor="acct-discord">
            <Input id="acct-discord" className="font-mono" autoComplete="off" value={discord} onChange={(e) => setDiscord(e.target.value)} />
          </Field>
          <ErrorText>{error}</ErrorText>
          <div className="flex items-center gap-3">
            <Button type="submit" size="sm" variant="outline" disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
            {done && <span className="text-ok" style={{ fontSize: 'var(--text-small)' }}>Saved.</span>}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

function SignOutEverywhere() {
  const [busy, setBusy] = useState(false)

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="font-semibold">Sign out everywhere</div>
        <Note>Includes this browser.</Note>
        <Button
          size="sm"
          variant="destructive"
          disabled={busy}
          onClick={() => {
            setBusy(true)
            void api.signOutEverywhere().finally(() => window.location.assign('/'))
          }}
        >
          {busy ? 'Signing out…' : 'Sign out everywhere'}
        </Button>
      </CardContent>
    </Card>
  )
}
