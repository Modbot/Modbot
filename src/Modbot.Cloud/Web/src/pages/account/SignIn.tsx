import { useState, type FormEvent } from 'react'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ApiError, api } from '@/lib/api'
import { go } from '@/lib/router'
import { Done, Field, Problem } from './Forms'

/** Signing in. The session is an HttpOnly cookie, so nothing here can read it. */
export function SignIn() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.signIn(email, password)
      setPassword('')
      go('/account')
    } catch (failure) {
      setPassword('')
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Card className="px-6">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <h1 className="font-display text-base">Sign in</h1>
          <Field
            id="email"
            label="Email"
            type="email"
            autoComplete="username"
            value={email}
            invalid={!!error}
            onChange={setEmail}
          />
          <Field
            id="password"
            label="Password"
            type="password"
            autoComplete="current-password"
            value={password}
            invalid={!!error}
            onChange={setPassword}
          />
          <Problem>{error}</Problem>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={busy || !email || !password}>
              Sign in
            </Button>
            <Link href="/register" className="text-link underline-offset-2 hover:underline">
              Create an account
            </Link>
            <Link href="/forgot-password" className="text-link underline-offset-2 hover:underline">
              Forgot password
            </Link>
          </div>
        </form>
      </Card>
    </Shell>
  )
}

/** Registering. The account cannot sign in until the address answers. */
export function Register() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.registerAccount(email, password)
      setPassword('')
      setSent(true)
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Card className="px-6">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <h1 className="font-display text-base">Create an account</h1>
          <Field
            id="email"
            label="Email"
            type="email"
            autoComplete="username"
            value={email}
            invalid={!!error}
            onChange={setEmail}
          />
          <Field
            id="password"
            label="Password"
            type="password"
            autoComplete="new-password"
            value={password}
            invalid={!!error}
            onChange={setPassword}
          />
          <Problem>{error}</Problem>
          <Done>{sent && `Check ${email}.`}</Done>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={busy || !email || !password}>
              Create account
            </Button>
            <Link href="/sign-in" className="text-link underline-offset-2 hover:underline">
              Sign in
            </Link>
          </div>
        </form>
      </Card>
    </Shell>
  )
}

/** Asking for a reset link. Says the same thing whether or not the address has an account. */
export function ForgotPassword() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.forgotPassword(email)
      setSent(true)
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Card className="px-6">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <h1 className="font-display text-base">Forgot password</h1>
          <Field
            id="email"
            label="Email"
            type="email"
            autoComplete="username"
            value={email}
            invalid={!!error}
            onChange={setEmail}
          />
          <Problem>{error}</Problem>
          <Done>{sent && `Check ${email}.`}</Done>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={busy || !email}>
              Send link
            </Button>
            <Link href="/sign-in" className="text-link underline-offset-2 hover:underline">
              Sign in
            </Link>
          </div>
        </form>
      </Card>
    </Shell>
  )
}
