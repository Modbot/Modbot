import { useState, type FormEvent } from 'react'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, api } from '@/lib/api'

/**
 * Signs in with ROOT_API_KEY. The key goes to the server once and is dropped from the form; the
 * session is an HttpOnly cookie, so nothing here can read it and nothing is written to storage.
 */
export function Login({ onSignedIn }: { onSignedIn: () => void }) {
  const [key, setKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!key) return

    setBusy(true)
    setError(null)
    try {
      await api.login(key)
      setKey('')
      onSignedIn()
    } catch (failure) {
      setKey('')
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell>
      <Card className="px-6">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <h1 className="text-base font-display">Admin</h1>
          <div className="flex flex-col gap-1.5">
            <label htmlFor="root-key" className="font-medium">
              Root API key
            </label>
            <Input
              id="root-key"
              type="password"
              autoComplete="current-password"
              value={key}
              aria-invalid={error ? true : undefined}
              onChange={(e) => setKey(e.target.value)}
            />
            {error && (
              <p role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                {error}
              </p>
            )}
          </div>
          <div>
            <Button type="submit" disabled={busy || !key}>
              Sign in
            </Button>
          </div>
        </form>
      </Card>
    </Shell>
  )
}
