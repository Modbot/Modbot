import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ApiError, api, type AccountView, type ServerView } from '@/lib/api'
import { go } from '@/lib/router'
import { Done, Field, Problem } from './Forms'

/**
 * The signed-in account: its servers, and the code that claims one.
 *
 * The code comes from the Modbot server's own settings page. Cloud never calls a Modbot server, so
 * the code arriving from the server is what proves whoever typed it can already sign in there.
 */
export function Account() {
  const [account, setAccount] = useState<AccountView | null>(null)
  const [servers, setServers] = useState<ServerView[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api
      .me()
      .then((next) => {
        setAccount(next)
        return api.myServers()
      })
      .then((next) => setServers(next.items))
      .catch((failure: unknown) => {
        if (failure instanceof ApiError && failure.status === 401) {
          go('/sign-in')
          return
        }
        setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
      })
  }, [])

  useEffect(load, [load])

  const signOut = () => {
    api
      .signOut()
      .catch(() => {})
      .finally(() => go('/sign-in'))
  }

  return (
    <Shell>
      <Card className="flex flex-col gap-4 px-6">
        <h1 className="font-display text-base">Account</h1>
        <Problem>{error}</Problem>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
          <dt className="text-muted-foreground">Email</dt>
          <dd>{account?.email ?? '…'}</dd>
          {account?.pendingEmail && (
            <>
              <dt className="text-muted-foreground">Moving to</dt>
              <dd>{account.pendingEmail}</dd>
            </>
          )}
        </dl>
        <div>
          <Button type="button" variant="outline" onClick={signOut}>
            Sign out
          </Button>
        </div>
      </Card>

      <ClaimCard onClaimed={load} />

      <Card className="flex flex-col gap-3 px-6">
        <h2 className="font-display text-base">Servers</h2>
        {servers.length === 0 ? (
          <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            None yet.
          </p>
        ) : (
          servers.map((server) => <Server key={server.serverId} server={server} onChanged={load} />)
        )}
      </Card>

      <ChangePasswordCard />
      <ChangeEmailCard onChanged={load} />
    </Shell>
  )
}

function Server({ server, onChanged }: { server: ServerView; onChanged: () => void }) {
  const [error, setError] = useState<string | null>(null)

  const release = () => {
    api
      .unclaimServer(server.serverId)
      .then(onChanged)
      .catch((failure: unknown) =>
        setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.'),
      )
  }

  return (
    <div className="flex flex-col gap-2 border-b py-3 last:border-0" style={{ borderBottomWidth: 'var(--hairline)' }}>
      <div className="flex items-center gap-3">
        {server.groupIconUrl && (
          <img src={server.groupIconUrl} alt="" width={36} height={36} className="size-9 rounded-md" />
        )}
        <div className="min-w-0">
          <div className="truncate font-medium">{server.groupName ?? server.publicAddress ?? server.serverId}</div>
          <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {[server.publicAddress, server.version, server.groupId].filter(Boolean).join(' · ')}
          </div>
        </div>
      </div>
      <Problem>{error}</Problem>
      <div>
        <Button type="button" size="sm" variant="outline" onClick={release}>
          Remove
        </Button>
      </div>
    </div>
  )
}

function ClaimCard({ onClaimed }: { onClaimed: () => void }) {
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.claimServer(code)
      setCode('')
      onClaimed()
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="px-6">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <h2 className="font-display text-base">Add a server</h2>
        <Field id="link-code" label="Link code" value={code} invalid={!!error} onChange={setCode} />
        <Problem>{error}</Problem>
        <div>
          <Button type="submit" disabled={busy || !code}>
            Add
          </Button>
        </div>
      </form>
    </Card>
  )
}

function ChangePasswordCard() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setDone(false)

    try {
      await api.changePassword(current, next)
      setCurrent('')
      setNext('')
      setDone(true)
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="px-6">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <h2 className="font-display text-base">Change password</h2>
        <Field
          id="current-password"
          label="Current password"
          type="password"
          autoComplete="current-password"
          value={current}
          invalid={!!error}
          onChange={setCurrent}
        />
        <Field
          id="new-password"
          label="New password"
          type="password"
          autoComplete="new-password"
          value={next}
          invalid={!!error}
          onChange={setNext}
        />
        <Problem>{error}</Problem>
        <Done>{done && 'Password changed.'}</Done>
        <div>
          <Button type="submit" disabled={busy || !current || !next}>
            Save
          </Button>
        </div>
      </form>
    </Card>
  )
}

function ChangeEmailCard({ onChanged }: { onChanged: () => void }) {
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
      await api.changeEmail(email, password)
      setPassword('')
      setSent(true)
      onChanged()
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="px-6">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <h2 className="font-display text-base">Change email</h2>
        <Field
          id="new-email"
          label="New email"
          type="email"
          autoComplete="email"
          value={email}
          invalid={!!error}
          onChange={setEmail}
        />
        <Field
          id="email-password"
          label="Password"
          type="password"
          autoComplete="current-password"
          value={password}
          invalid={!!error}
          onChange={setPassword}
        />
        <Problem>{error}</Problem>
        <Done>{sent && `Check ${email}.`}</Done>
        <div>
          <Button type="submit" disabled={busy || !email || !password}>
            Save
          </Button>
        </div>
      </form>
    </Card>
  )
}
