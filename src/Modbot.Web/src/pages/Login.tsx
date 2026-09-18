import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api } from '@/lib/api'
import { Brand, ErrorText, Field, Tickbox, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/**
 * Signing in (spec 7.2).
 *
 * Shares the wizard's card rather than inventing a second full-screen layout: they are the two
 * screens outside the app shell, and an operator who has just been through one should recognise
 * the other.
 */
export function Login({
  onSignedIn,
  onForgotPassword,
}: {
  onSignedIn: () => void
  onForgotPassword: () => void
}) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  // Unticked by default. A moderation tool gets opened on borrowed machines, and a session that
  // outlives the browser is the riskier of the two, so it is asked for rather than assumed.
  const [keepSignedIn, setKeepSignedIn] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)

    api
      .login({ username, password, keepSignedIn })
      .then(onSignedIn)
      .catch((e: unknown) => {
        // The server answers 401 for every kind of failure without saying which, so that a login
        // form cannot be used to find out which usernames are real -- or, now that the field also
        // takes an address, who has an account here. The message here says the same thing rather
        // than guessing at something more specific.
        setError(
          e instanceof ApiError && e.status === 401
            ? 'That sign-in and password do not match.'
            : e instanceof ApiError
              ? e.message
              : 'Could not reach the Modbot server.',
        )
      })
      .finally(() => setBusy(false))
  }

  return (
    <div className="grid min-h-screen place-items-center bg-background p-6">
      <div className="w-full max-w-[420px]">
        <Brand />
        <form
          onSubmit={submit}
          className="overflow-hidden rounded-xl border bg-card shadow-lg"
        >
          <WizardHeader eyebrow="Sign in" title="Welcome back" />
          <WizardBody>
            <Field label="Email or username" htmlFor="login-username">
              <Input
                id="login-username"
                autoComplete="username"
                autoFocus
                required
                value={username}
                onChange={(e) => setUsername(e.target.value)}
              />
            </Field>
            <Field label="Password" htmlFor="login-password">
              <Input
                id="login-password"
                type="password"
                autoComplete="current-password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </Field>
            <Tickbox id="login-keep-signed-in" checked={keepSignedIn} onChange={setKeepSignedIn}>
              Keep me signed in
            </Tickbox>
            <ErrorText>{error}</ErrorText>
          </WizardBody>
          <WizardFooter>
            <Button
              type="button"
              variant="ghost"
              onClick={onForgotPassword}
              style={{ height: 'var(--control-h)' }}
            >
              Forgot password?
            </Button>
            <div className="flex-1" />
            <Button type="submit" disabled={busy} style={{ height: 'var(--control-h)' }}>
              {busy ? 'Signing in…' : 'Sign in'}
            </Button>
          </WizardFooter>
        </form>
      </div>
    </div>
  )
}
