import { useCallback, useEffect, useState } from 'react'
import { Sidebar, Topbar, type PageId } from '@/components/Chrome'
import { SubjectPane } from '@/components/SubjectPane'
import { api, type OnboardingStatus } from '@/lib/api'
import { usePreferences } from '@/lib/preferences'
import { useQueryParam, useRoute } from '@/lib/router'
import { AuditLog } from '@/pages/AuditLog'
import { Bans } from '@/pages/Bans'
import { Health } from '@/pages/Health'
import { Login } from '@/pages/Login'
import { Members } from '@/pages/Members'
import { Settings } from '@/pages/Settings'
import { Setup } from '@/pages/setup/Setup'

const TITLES: Record<PageId, { title: string; subtitle?: string }> = {
  members: { title: 'Members' },
  bans: { title: 'Bans', subtitle: 'What Modbot recorded — not the group’s ban list' },
  audit: { title: 'Audit log', subtitle: 'One timeline, merged across sources' },
  health: { title: 'Sync health' },
  settings: { title: 'Settings' },
}

/**
 * Pages live at real paths so the subject pane's deep link means something: `/audit?subject=usr_…`
 * survives a refresh and can be pasted to another moderator (spec 10.2). A pane whose URL put you
 * back on the members list would be a pane nobody shares.
 */
const PATHS: Record<PageId, string> = {
  members: '/',
  bans: '/bans',
  audit: '/audit',
  health: '/health',
  settings: '/settings',
}

function pageFor(path: string): PageId {
  const match = (Object.keys(PATHS) as PageId[]).find((id) => PATHS[id] === path)
  return match ?? 'members'
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

  return <Shell status={status} prefs={prefs} route={route} navigate={navigate} />
}

function Shell({
  status,
  prefs,
  route,
  navigate,
}: {
  status: OnboardingStatus
  prefs: ReturnType<typeof usePreferences>
  route: string
  navigate: (to: string, options?: { replace?: boolean }) => void
}) {
  const page = pageFor(route)
  const { title, subtitle } = TITLES[page]

  // The pane is a query parameter rather than component state, so it is linkable and survives a
  // refresh. Every list that renders a person opens it the same way (spec 10.2).
  const [subject, setSubject] = useQueryParam('subject')

  return (
    <div className="grid h-screen grid-cols-[13.5rem_1fr]">
      <Sidebar page={page} onNavigate={(p) => navigate(PATHS[p])} groupName={status.group?.name} />
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
          {page === 'bans' && <Bans onOpenSubject={setSubject} />}
          {page === 'audit' && <AuditLog onOpenSubject={setSubject} />}
          {page === 'health' && <Health />}
          {page === 'settings' && <Settings />}
        </div>
      </main>

      {subject && <SubjectPane key={subject} subjectId={subject} onClose={() => setSubject(null)} />}
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
