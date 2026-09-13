import { useCallback, useEffect, useState } from 'react'
import { Sidebar, Topbar, type PageId } from '@/components/Chrome'
import { api, type OnboardingStatus } from '@/lib/api'
import { usePreferences } from '@/lib/preferences'
import { useRoute } from '@/lib/router'
import { Login } from '@/pages/Login'
import { Members } from '@/pages/Members'
import { Placeholder } from '@/pages/Placeholder'
import { Settings } from '@/pages/Settings'
import { Setup } from '@/pages/setup/Setup'

const TITLES: Record<PageId, { title: string; subtitle?: string }> = {
  members: { title: 'Members' },
  bans: { title: 'Bans' },
  audit: { title: 'Audit log' },
  metrics: { title: 'Metrics' },
  settings: { title: 'Settings' },
}

export default function App() {
  const [route, navigate] = useRoute()
  const [status, setStatus] = useState<OnboardingStatus | null>(null)

  // Theme and density are applied here rather than inside the app shell, so the wizard and the
  // sign-in page are themed too. An operator who set Modbot to dark and then re-ran a setup step
  // should not be handed a white screen at midnight.
  const prefs = usePreferences()

  const refresh = useCallback(
    () =>
      api.onboardingStatus().then((next) => {
        setStatus(next)
        return next
      }),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Spec 7.1: with no staff account present, every route leads to the wizard -- there is nobody
  // to authenticate as, so there is nothing else the deployment can usefully show. A signed-in
  // operator whose setup was never finished goes there too, because an app shell with no group
  // configured has nothing in it.
  useEffect(() => {
    if (!status || route === '/setup') return

    const unfinished = !status.hasAdministrator || (status.authenticated && !status.onboardingComplete)

    if (unfinished) navigate('/setup', { replace: true })
  }, [status, route, navigate])

  if (!status) return <Booting />

  if (route === '/setup') {
    return (
      <Setup
        // Refreshed *before* navigating, not alongside it. Leaving on a stale status means the
        // redirect below still believes setup is unfinished and bounces straight back into the
        // wizard -- which, from the operator's side, is "I pressed Finish and nothing happened".
        onFinished={() => void refresh().then(() => navigate('/'))}
      />
    )
  }

  if (!status.authenticated) {
    return (
      <Login
        // Same ordering, same reason. Somebody who signed in on a deployment that was never
        // finished lands back in the wizard at whichever step is outstanding, rather than in an
        // app shell with no group configured.
        onSignedIn={() =>
          void refresh().then((next) => navigate(next.onboardingComplete ? '/' : '/setup'))
        }
      />
    )
  }

  return <Shell status={status} prefs={prefs} />
}

function Shell({
  status,
  prefs,
}: {
  status: OnboardingStatus
  prefs: ReturnType<typeof usePreferences>
}) {
  const [page, setPage] = useState<PageId>('members')
  const { title, subtitle } = TITLES[page]

  return (
    <div className="grid h-screen grid-cols-[13.5rem_1fr]">
      <Sidebar page={page} onNavigate={setPage} groupName={status.group?.name} />
      <main className="flex flex-col overflow-auto">
        <Topbar
          title={title}
          subtitle={subtitle}
          {...prefs}
          // A full reload rather than a state change: signing out invalidates the cookie, and
          // every cached page in memory was rendered for the person who just left.
          onSignOut={() => void api.logout().finally(() => window.location.assign('/'))}
        />
        <div className="p-5">
          {page === 'members' && <Members />}
          {page === 'settings' && <Settings />}
          {page !== 'members' && page !== 'settings' && <Placeholder name={title} />}
        </div>
      </main>
    </div>
  )
}

function Booting() {
  return (
    <div
      className="grid min-h-screen place-items-center bg-background text-muted-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      Loading…
    </div>
  )
}
