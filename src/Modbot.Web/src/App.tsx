import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import { Footer, Sidebar, Topbar } from '@/components/Chrome'
import { CommandPalette, type PaletteAction } from '@/components/CommandPalette'
import { ShortcutSheet } from '@/components/ShortcutSheet'
import { SignInWaitBanner } from '@/components/SignInWaitBanner'
import { SubjectPopup } from '@/components/subject/SubjectPopup'
import { api, type CurrentUser, type OnboardingStatus } from '@/lib/api'
import { DemoContext } from '@/lib/demo'
import { REVIEW_KINDS, type LiveEvent } from '@/lib/liveStream'
import { setMyModbotOrigin } from '@/lib/myModbot'
import { CREDITS_PATH, GO_TO_KEYS, MOVED, NAV, mayOpen, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { usePreferences, type Density } from '@/lib/preferences'
import { go, useRoute } from '@/lib/router'
import { useKeyboard, useShortcuts } from '@/lib/shortcuts'
import type { StatusRowId } from '@/lib/status'
import { openPerson } from '@/lib/subject'
import { useLiveStream } from '@/lib/useLiveStream'
import { Account } from '@/pages/Account'
import { AuditLog } from '@/pages/AuditLog'
import { Bans } from '@/pages/Bans'
import { CaseFile } from '@/pages/CaseFile'
// Chat carries a Markdown highlighter nothing else uses, so it is fetched when it is opened.
const Chat = lazy(() => import('@/pages/Chat').then((m) => ({ default: m.Chat })))
import { Credits } from '@/pages/Credits'
import { ForgotPassword } from '@/pages/ForgotPassword'
import { Health } from '@/pages/Health'
import { Join } from '@/pages/Join'
import { LinkAccounts } from '@/pages/LinkAccounts'
import { LinkVRChat } from '@/pages/LinkVRChat'
import { Live } from '@/pages/Live'
import { Calendar } from '@/pages/Calendar'
import { Login } from '@/pages/Login'
import { Connect } from '@/pages/Connect'
import { Logs } from '@/pages/Logs'
import { DiscordMembers } from '@/pages/DiscordMembers'
import { Members } from '@/pages/Members'
import { Instances } from '@/pages/analytics/Instances'
import { MyGroup } from '@/pages/analytics/MyGroup'
import { MyServer } from '@/pages/analytics/MyServer'
import { MyTeam } from '@/pages/analytics/MyTeam'
import { Worlds } from '@/pages/analytics/Worlds'
import { Pair } from '@/pages/Pair'
import { ResetPassword } from '@/pages/ResetPassword'
import { Flags } from '@/pages/Flags'
import { Reviews } from '@/pages/Reviews'
import { Roles } from '@/pages/Roles'
import { Settings } from '@/pages/Settings'
import { Users } from '@/pages/Users'
import { Setup } from '@/pages/setup/Setup'

const TITLES: Record<PageId, string> = {
  members: 'Members',
  'discord-members': 'Discord members',
  live: 'Live',
  calendar: 'Calendar',
  chat: 'Chat',
  bans: 'Bans',
  flags: 'Flags',
  audit: 'Audit log',
  'analytics-group': 'My Group',
  'analytics-server': 'My Server',
  'analytics-team': 'My Team',
  'analytics-worlds': 'Worlds',
  'analytics-instances': 'Instances',
  reviews: 'Reviews',
  users: 'Users',
  roles: 'Roles',
  health: 'Sync health',
  logs: 'Logs',
  settings: 'Settings',
  account: 'Your account',
  cases: 'Case file',
  credits: 'Credits',
}

/**
 * Pages live at real paths so the popup's link means something: `/audit?subject=usr_…` survives a
 * refresh and can be pasted to another moderator (spec 10.2). A popup whose URL put you back on the
 * members list would be a popup nobody shares.
 */
const PATHS: Record<PageId, string> = {
  members: '/',
  'discord-members': '/discord/members',
  live: '/live',
  calendar: '/calendar',
  chat: '/chat',
  bans: '/bans',
  flags: '/flags',
  audit: '/audit',
  'analytics-group': '/analytics/group',
  'analytics-server': '/analytics/server',
  'analytics-team': '/analytics/team',
  'analytics-worlds': '/analytics/worlds',
  'analytics-instances': '/analytics/instances',
  reviews: '/reviews',
  users: '/users',
  roles: '/roles',
  health: '/health',
  logs: '/logs',
  settings: '/settings',
  account: '/account',
  cases: '/cases',
  credits: CREDITS_PATH,
}

/**
 * A case file lives at `/cases/:id` so it can be pasted to another moderator, the same reason the
 * subject pane carries its subject in the query string (spec 10.2). It is the only page with an
 * id in its path, so the router grows one line rather than a route table.
 */
function caseFileId(path: string): string | null {
  const prefix = `${PATHS.cases}/`
  return path.startsWith(prefix) && path.length > prefix.length ? path.slice(prefix.length) : null
}

/**
 * A conversation lives at `/chat/:id` for the same reason a case file does: it is the person's own
 * thread, and they come back to it.
 */
function chatConversationId(path: string): string | null {
  const prefix = `${PATHS.chat}/`
  return path.startsWith(prefix) && path.length > prefix.length ? path.slice(prefix.length) : null
}

function pageFor(path: string): PageId {
  if (caseFileId(path)) return 'cases'
  if (chatConversationId(path)) return 'chat'

  const wanted = MOVED[path] ?? path
  const match = (Object.keys(PATHS) as PageId[]).find((id) => PATHS[id] === wanted)
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

  // A demo has no sign-in and no session, so it has nothing to sign out of. False everywhere else.
  const [demo, setDemo] = useState(false)

  useEffect(() => {
    api
      .demoStatus()
      .then((d) => setDemo(d.on))
      .catch(() => undefined)
  }, [])

  // Theme and density are applied here rather than inside the app shell, so the wizard and the
  // sign-in page are themed too. An operator who set Modbot to dark and then re-ran a setup step
  // should not be handed a white screen at midnight.
  const prefs = usePreferences()

  const refresh = useCallback(async () => {
    const next = await api.onboardingStatus()
    setStatus(next)

    // Every link to the selector is built from this, so it is set before anything renders.
    setMyModbotOrigin(next.myModbotUrl)

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
    if (!status || route === '/setup' || route === '/link' || link) return

    const unfinished = !status.hasAdministrator || (status.authenticated && !status.onboardingComplete)

    if (unfinished) navigate('/setup', { replace: true })
  }, [status, route, navigate, link])

  // The member link page (Discord account linking design §3). For any community member, signed in
  // to Modbot or not, and whether or not this deployment's own setup is finished.
  if (route === '/link') return <LinkAccounts />

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
            navigate(
              next.onboardingComplete
                ? route === '/pair' || route === '/connect'
                  ? route + window.location.search
                  : '/'
                : '/setup',
            ),
          )
        }
        onForgotPassword={() => navigate('/forgot-password')}
      />
    )
  }

  if (!me) return <Booting />

  // Signed in, set up, but not yet linked to a VRChat account: the one page they can use
  // (design §4.3). The wizard handles this for the first administrator; this is everybody after.
  // It comes before the pairing page on purpose: a companion reports presence under the
  // moderator's VRChat identity, so an unlinked account has nothing to pair as yet.
  if (!me.vrChatLinked) return <LinkVRChat me={me} onLinked={() => void refresh()} />

  // Outside the shell, like sign-in: a landing page a link sends a moderator to, not a section
  // of the app they navigate around in.
  if (route === '/pair') return <Pair />

  // An AI app asking to act as this person on the MCP server (MCP server design). A landing page
  // like pairing: the app sent the browser here, and it leaves for the app's address.
  if (route === '/connect') return <Connect />

  return (
    <DemoContext value={demo}>
      <Shell
        status={status}
        me={me}
        prefs={prefs}
        route={route}
        navigate={navigate}
        refresh={refresh}
        demo={demo}
      />
    </DemoContext>
  )
}

function Shell({
  status,
  me,
  prefs,
  route,
  navigate,
  refresh,
  demo,
}: {
  status: OnboardingStatus
  me: CurrentUser
  prefs: ReturnType<typeof usePreferences>
  route: string
  navigate: (to: string, options?: { replace?: boolean }) => void
  refresh: () => Promise<OnboardingStatus>
  demo: boolean
}) {
  const requested = pageFor(route)

  // An address that moved still opens its page, and the bar quietly becomes the new address -- so
  // an old bookmark works and what a moderator copies out afterwards is the one that will last.
  useEffect(() => {
    const moved = MOVED[route]
    if (moved) navigate(moved, { replace: true })
  }, [route, navigate])

  // A page this person may not open shows the first one they may. The server refuses the data
  // regardless; this only keeps the shell from rendering an empty page with an error in it.
  const page = mayOpen(me, requested)
    ? requested
    : (NAV.find((n) => !('hidden' in n && n.hidden) && mayOpen(me, n.id))?.id ?? 'account')
  const title = TITLES[page]

  // The popup lives in the query string rather than in component state, so it is linkable, survives
  // a refresh, and stacks (spec 10.2, lib/subject.ts). Every list that renders a person opens it the
  // same way; worlds and instances open themselves through the same module.
  const setSubject = openPerson

  // The number beside "Reviews": how many are waiting for somebody to look. Read when the shell
  // mounts and whenever the page changes, so closing one on the Reviews page updates it without
  // a poll; only asked for by people who could open the page.
  const canReview = can(me, 'ReviewTickets')
  const [openReviews, setOpenReviews] = useState(0)
  const refreshReviewCount = useCallback(() => {
    if (!canReview) return
    api.openReviewCount().then((c) => setOpenReviews(c.open)).catch(() => undefined)
  }, [canReview])

  useEffect(() => {
    refreshReviewCount()
  }, [refreshReviewCount, page])

  // And whenever a review opens or closes anywhere, from the live stream.
  useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (REVIEW_KINDS.has(event.kind)) refreshReviewCount()
      },
      [refreshReviewCount],
    ),
  )

  // The keyboard (lib/shortcuts.ts): the palette, the sheet, and `g` then a letter for every page
  // this person may open. Pages register their own list and filter keys.
  useKeyboard()

  const [paletteOpen, setPaletteOpen] = useState(false)
  const [sheetOpen, setSheetOpen] = useState(false)

  const signOut = demo ? undefined : () => void api.logout().finally(() => window.location.assign('/'))

  useShortcuts([
    { keys: 'mod+k', label: 'Search and commands', group: 'General', run: () => setPaletteOpen((o) => !o) },
    { keys: '?', label: 'Keyboard shortcuts', group: 'General', run: () => setSheetOpen((o) => !o) },
    ...NAV.filter((n) => !('hidden' in n && n.hidden) && mayOpen(me, n.id) && GO_TO_KEYS[n.id]).map((n) => ({
      keys: `g ${GO_TO_KEYS[n.id]}`,
      label: n.label,
      group: 'Go to' as const,
      run: () => navigate(PATHS[n.id]),
    })),
  ])

  const densities: { value: Density; label: string }[] = [
    { value: 'dense', label: 'Dense' },
    { value: 'comfortable', label: 'Comfortable' },
    { value: 'vr', label: 'VR' },
  ]

  const paletteActions: PaletteAction[] = [
    {
      id: 'theme',
      label: prefs.theme === 'dark' ? 'Light theme' : 'Dark theme',
      group: 'Appearance',
      run: () => prefs.setTheme(prefs.theme === 'dark' ? 'light' : 'dark'),
    },
    ...densities
      .filter((d) => d.value !== prefs.density)
      .map((d) => ({ id: `density:${d.value}`, label: `${d.label} density`, group: 'Appearance', run: () => prefs.setDensity(d.value) })),
    { id: 'account', label: 'Your account', group: 'Account', run: () => navigate(PATHS.account) },
    ...(signOut ? [{ id: 'sign-out', label: 'Sign out', group: 'Account', run: signOut }] : []),
  ]

  return (
    <div className="grid h-screen grid-cols-[13.5rem_1fr]">
      <Sidebar
        page={page}
        me={me}
        onNavigate={(p) => navigate(PATHS[p])}
        onSearch={() => setPaletteOpen(true)}
        // `go` rather than `navigate`: the hash names the card to open, and only `go` wakes the
        // page already on screen when nothing but the hash changed.
        onOpenHealth={(section: StatusRowId) => go(`${PATHS.health}#${section}`)}
        group={status.group}
        badges={{ reviews: openReviews }}
      />
      <main className="flex flex-col overflow-auto">
        {/* Above everything, for everyone signed in, on every page (foundation spec 4.1.2). */}
        <SignInWaitBanner />
        <Topbar
          title={title}
          {...prefs}
          username={me.username}
          onAccount={() => navigate(PATHS.account)}
          // A full reload rather than a state change: signing out invalidates the cookie, and
          // every cached page in memory was rendered for the person who just left. A demo has no
          // session to end, so the control is not there.
          onSignOut={signOut}
        />
        <div className="p-5">
          {page === 'members' && <Members me={me} onOpenSubject={setSubject} />}
          {page === 'discord-members' && <DiscordMembers me={me} />}
          {page === 'live' && <Live />}
          {page === 'calendar' && <Calendar />}
          {page === 'chat' && (
            <Suspense fallback={null}>
              <Chat
                conversationId={chatConversationId(route)}
                onOpenConversation={(id, options) =>
                  navigate(id ? `${PATHS.chat}/${id}` : PATHS.chat, options)
                }
              />
            </Suspense>
          )}
          {page === 'bans' && (
            <Bans me={me} onOpenSubject={setSubject} onOpenCase={(id) => navigate(`${PATHS.cases}/${id}`)} />
          )}
          {page === 'cases' && (
            <CaseFile
              key={caseFileId(route) ?? ''}
              caseId={caseFileId(route) ?? ''}
              onOpenSubject={setSubject}
              onBack={() => navigate(PATHS.bans)}
            />
          )}
          {page === 'flags' && <Flags me={me} onOpenSubject={setSubject} />}
          {page === 'audit' && <AuditLog />}
          {page === 'analytics-group' && <MyGroup />}
          {page === 'analytics-server' && <MyServer />}
          {page === 'analytics-team' && (
            <MyTeam onOpenSubject={setSubject} onOpenReviews={canReview ? () => navigate(PATHS.reviews) : undefined} />
          )}
          {page === 'analytics-worlds' && <Worlds />}
          {page === 'analytics-instances' && <Instances />}
          {page === 'reviews' && <Reviews onOpenSubject={setSubject} onChanged={refreshReviewCount} />}
          {page === 'users' && <Users me={me} />}
          {page === 'roles' && <Roles me={me} />}
          {page === 'health' && <Health />}
          {page === 'logs' && <Logs />}
          {page === 'settings' && <Settings />}
          {page === 'account' && <Account me={me} onChanged={() => void refresh()} />}
          {page === 'credits' && <Credits />}
        </div>
        <Footer />
      </main>

      {/* Over the page, never instead of it: the page stays mounted with its scroll position and
          filters, so closing the popup puts the moderator back exactly where they were. */}
      <SubjectPopup me={me} />

      <CommandPalette
        open={paletteOpen}
        onOpenChange={setPaletteOpen}
        me={me}
        onGoTo={(p) => navigate(PATHS[p])}
        actions={paletteActions}
      />
      <ShortcutSheet open={sheetOpen} onOpenChange={setSheetOpen} />
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
