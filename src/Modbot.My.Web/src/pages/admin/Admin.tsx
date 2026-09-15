import { useCallback, useEffect, useState } from 'react'
import { Link } from '@/components/Link'
import { Mark, Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { api } from '@/lib/api'
import { go } from '@/lib/router'
import { SignedOutContext } from '@/lib/useLoad'
import { cn } from '@/lib/utils'
import { InstanceDetail } from './InstanceDetail'
import { Instances } from './Instances'
import { Login } from './Login'
import { RegisterPageInstances } from './RegisterPageInstances'
import { Stats } from './Stats'

const NAV = [
  { href: '/admin', label: 'Stats' },
  { href: '/admin/instances', label: 'Instances' },
  { href: '/admin/register-page', label: 'Register page' },
]

type SessionState = 'checking' | 'signed-out' | 'signed-in'

export function Admin({ path }: { path: string }) {
  const [state, setState] = useState<SessionState>('checking')

  useEffect(() => {
    api
      .session()
      .then(() => setState('signed-in'))
      .catch(() => setState('signed-out'))
  }, [])

  const signedOut = useCallback(() => setState('signed-out'), [])

  if (state === 'checking') {
    return (
      <Shell>
        <Card className="px-6">
          <h1 className="text-base font-semibold">Loading</h1>
        </Card>
      </Shell>
    )
  }

  if (state === 'signed-out') return <Login onSignedIn={() => setState('signed-in')} />

  const logout = async () => {
    await api.logout().catch(() => {})
    setState('signed-out')
    go('/admin')
  }

  return (
    <SignedOutContext.Provider value={signedOut}>
      <div className="min-h-screen bg-background">
        <header className="border-b bg-card">
          <div className="mx-auto flex h-14 max-w-5xl items-center gap-4 px-4">
            <Link href="/admin" className="flex items-center gap-2 font-semibold">
              <Mark />
              <span className="hidden sm:inline">Admin</span>
            </Link>
            <nav className="flex gap-1">
              {NAV.map((item) => {
                const active = item.href === '/admin' ? path === '/admin' : path.startsWith(item.href)
                return (
                  <Link
                    key={item.href}
                    href={item.href}
                    className={cn(
                      'rounded-md px-2.5 py-1.5 font-medium transition-colors',
                      active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-secondary hover:text-foreground',
                    )}
                  >
                    {item.label}
                  </Link>
                )
              })}
            </nav>
            <Button variant="ghost" size="sm" className="ml-auto" onClick={logout}>
              Log out
            </Button>
          </div>
        </header>
        <main className="mx-auto flex max-w-5xl flex-col gap-4 px-4 py-6">
          <AdminPage path={path} />
        </main>
      </div>
    </SignedOutContext.Provider>
  )
}

function AdminPage({ path }: { path: string }) {
  if (path === '/admin') return <Stats />
  if (path === '/admin/instances') return <Instances />
  if (path.startsWith('/admin/instances/'))
    return <InstanceDetail instanceId={decodeURIComponent(path.slice('/admin/instances/'.length))} />
  if (path === '/admin/register-page') return <RegisterPageInstances />
  return <h1 className="text-lg font-display">Page not found</h1>
}
