import { useCallback, useEffect, useState } from 'react'
import { Sidebar, Topbar } from '@/components/Chrome'
import { SubjectPane } from '@/components/SubjectPane'
import { api, type CurrentUser, type OnboardingStatus } from '@/lib/api'
import { NAV, mayOpen, type PageId } from '@/lib/nav'
import { usePreferences } from '@/lib/preferences'
import { useQueryParam, useRoute } from '@/lib/router'
import { Account } from '@/pages/Account'
import { AuditLog } from '@/pages/AuditLog'
import { Bans } from '@/pages/Bans'
import { ForgotPassword } from '@/pages/ForgotPassword'
import { Health } from '@/pages/Health'
import { Join } from '@/pages/Join'
import { LinkVRChat } from '@/pages/LinkVRChat'
import { Login } from '@/pages/Login'
import { Members } from '@/pages/Members'
import { Metrics } from '@/pages/Metrics'
import { Pair } from '@/pages/Pair'
import { ResetPassword } from '@/pages/ResetPassword'
import { Roles } from '@/pages/Roles'
import { Settings } from '@/pages/Settings'
import { Users } from '@/pages/Users'
import { Setup } from '@/pages/setup/Setup'

const TITLES: Record<PageId, { title: string; subtitle?: string }> = {
  members: { title: 'Members' },
  bans: { title: 'Bans', subtitle: 'What Modbot recorded — not the group’s ban list' },
  audit: { title: 'Audit log', subtitle: 'One timeline, merged across sources' },
  metrics: { title: 'Metrics' },
  users: { title: 'Users', subtitle: 'Who can sign in to this Modbot, and what they can do' },
  roles: { title: 'Roles', subtitle: 'What each role allows' },
  health: { title: 'Sync health' },
  settings: { title: 'Settings' },
  account: { title: 'Your account' },
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
  metrics: '/metrics',
  users: '/users',
  roles: '/roles',
  health: '/health',
  settings: '/settings',
  account: '/account',
}

function pageFor(path: string): PageId {
  const match = (Object.keys(PATHS) as PageId[]).find((id) => PATHS[id] === path)
  return match ?? 'members'
}

/** The pages that live outside the app shell and need no session: a link somebody was sent. */
function tokenRoute(path: string): { kind: 'join' | 'reset'; token: string } | null {
  for (const kind of ['join', 'reset'] as const) {
    const prefix = `/${kind}/`
    if (path.startsWith(prefix) && path.length > prefix.length)
      return { kind, token: decodeURIComponent(path.slice(prefix.length)) }
  }
  return null
}

export default function App() {
  const [route, navigate] = useRoute()
  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [me, setMe] = useState<CurrentUser | null>(null)

  // Theme and density are applied here rather than inside the app shell, so the wizard and the
  // sign-in page are themed too. An operator who set Modbot to dark and then re-ran a setup step
  // should not be handed a white screen at midnight.
  const prefs = usePreferences()

  const refresh = useCallback(async () => {
    const next = await api.onboardingStatus()
    setStatus(next)

    // Who is signed in decides what the shell shows (accounts and access design §8). Read
    // alongside status so a permission change, a rename or a fresh VRChat link shows up on the
    // next refresh rather than the next sign-in.
    setMe(next.authenticated ? await api.me().catch(() => null) : null)
    return next
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const link = tokenRoute(route)

  // Spec 7.1: with no staff account present, every route leads to the wizard -- there is nobody
  // to authenticate as, so there is nothing else the deployment can usefully show. A signed-in
  // operator whose setup was never finished goes there too, because an app shell with no group
  // configured has nothing in it.
  useEffect(() => {
    if (!status || route === '/setup' || link) return

    const unfinished = !status.hasAdministrator || (status.authenticated && !status.onboardingComplete)

    if (unfinished) navigate('/setup', { replace: true })
  }, [status, route, navigate, link])

  if (link?.kind === 'join')
    return <Join token={link.token} onJoined={() => void refresh().then(() => navigate('/'))} />
  if (link?.kind === 'reset') return <ResetPassword token={link.token} />

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
    if (route === '/forgot-password') return <ForgotPassword onBack={() => navigate('/')} />

    return (
      <Login
        // Same ordering, same reason. Somebody who signed in on a deployment that was never
        // finished lands back in the wizard at whichever step is outstanding, rather than in an
        // app shell with no group configured.
        // A moderator sent to /pair by a link signs in and lands back on /pair, not on the
        // members list: the link was the errand, and the page it names is where it finishes.
        onSignedIn={() =>
          void refresh().then((next) =>
            navigate(next.onboardingComplete ? (route === '/pair' ? '/pair' : '/') : '/setup'),
          )
        }
        onForgotPassword={() => navigate('/forgot-password')}
      />
    )
  }

  if (!me) return <Booting />

  // Signed in, set up, but not yet linked to a VRChat account: the one page they can use
  // (design §4.3). The wizard handles this for the first administrator; this is everybody after.
  // It comes before the pairing page on purpose: a desktop client reports presence under the
  // moderator's VRChat identity, so an unlinked account has nothing to pair as yet.
  if (!me.vrChatLinked) return <LinkVRChat me={me} onLinked={() => void refresh()} />

  // Outside the shell, like sign-in: a landing page a link sends a moderator to, not a section
  // of the app they navigate around in.
  if (route === '/pair') return <Pair />

  return <Shell status={status} me={me} prefs={prefs} route={route} navigate={navigate} refresh={refresh} />
}

function Shell({
  status,
  me,
  prefs,
  route,
  navigate,
  refresh,
}: {
  status: OnboardingStatus
  me: CurrentUser
  prefs: ReturnType<typeof usePreferences>
  route: string
  navigate: (to: string, options?: { replace?: boolean }) => void
  refresh: () => Promise<OnboardingStatus>
}) {
  const requested = pageFor(route)

  // A page this person may not open shows the first one they may. The server refuses the data
  // regardless; this only keeps the shell from rendering an empty page with an error in it.
  const page = mayOpen(me, requested)
    ? requested
    : (NAV.find((n) => !('hidden' in n && n.hidden) && mayOpen(me, n.id))?.id ?? 'account')
  const { title, subtitle } = TITLES[page]

  // The pane is a query parameter rather than component state, so it is linkable and survives a
  // refresh. Every list that renders a person opens it the same way (spec 10.2).
  const [subject, setSubject] = useQueryParam('subject')

  return (
    <div className="grid h-screen grid-cols-[13.5rem_1fr]">
      <Sidebar page={page} me={me} onNavigate={(p) => navigate(PATHS[p])} groupName={status.group?.name} />
      <main className="flex flex-col overflow-auto">
        <Topbar
          title={title}
          subtitle={subtitle}
          {...prefs}
          username={me.username}
          onAccount={() => navigate(PATHS.account)}
          // A full reload rather than a state change: signing out invalidates the cookie, and
          // every cached page in memory was rendered for the person who just left.
          onSignOut={() => void api.logout().finally(() => window.location.assign('/'))}
        />
        <div className="p-5">
          {page === 'members' && <Members />}
          {page === 'bans' && <Bans onOpenSubject={setSubject} />}
          {page === 'audit' && <AuditLog onOpenSubject={setSubject} />}
          {page === 'metrics' && <Metrics />}
          {page === 'users' && <Users me={me} />}
          {page === 'roles' && <Roles me={me} />}
          {page === 'health' && <Health />}
          {page === 'settings' && <Settings />}
          {page === 'account' && <Account me={me} onChanged={() => void refresh()} />}
        </div>
      </main>

      {subject && (
        <SubjectPane
          key={subject}
          subjectId={subject}
          me={me}
          onClose={() => setSubject(null)}
        />
      )}
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
