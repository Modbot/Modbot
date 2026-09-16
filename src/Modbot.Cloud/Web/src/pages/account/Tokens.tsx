import { useEffect, useState, type FormEvent } from 'react'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ApiError, api } from '@/lib/api'
import { Done, Field, Problem } from './Forms'

/** Where the confirmation link in Cloud's mail lands. */
export function Verify({ token, change }: { token: string | null; change: boolean }) {
  // A link with no code has already failed before the effect runs, so it starts that way rather
  // than rendering "working" for one frame.
  const [state, setState] = useState<'working' | 'done' | 'failed'>(token ? 'working' : 'failed')
  const [error, setError] = useState<string | null>(token ? null : 'That link is missing its code.')

  useEffect(() => {
    if (!token) return

    const confirm = change ? api.verifyEmailChange(token) : api.verifyEmail(token)

    confirm
      .then(() => setState('done'))
      .catch((failure: unknown) => {
        setState('failed')
        setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
      })
  }, [token, change])

  return (
    <Shell>
      <Card className="flex flex-col gap-4 px-6">
        <h1 className="font-display text-base">{change ? 'New address' : 'Confirm address'}</h1>
        {state === 'working' && <Done>Working…</Done>}
        {state === 'done' && <Done>Confirmed.</Done>}
        <Problem>{state === 'failed' && error}</Problem>
        <div>
          <Button asChild variant="outline">
            <Link href={change ? '/account' : '/sign-in'}>{change ? 'Account' : 'Sign in'}</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}

/** Where the reset link in Cloud's mail lands. Every session ends when the password changes. */
export function ResetPassword({ token }: { token: string | null }) {
  const [password, setPassword] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!token) {
      setError('That link is missing its code.')
      return
    }

    setBusy(true)
    setError(null)

    try {
      await api.resetPassword(token, password)
      setPassword('')
      setDone(true)
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
          <h1 className="font-display text-base">New password</h1>
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
          <Done>{done && 'Password changed.'}</Done>
          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={busy || !password || done}>
              Save
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
