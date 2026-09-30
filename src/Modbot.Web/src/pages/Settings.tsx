import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { DataSection } from '@/components/settings/DataSection'
import { EvidenceSection } from '@/components/settings/EvidenceSection'
import { IamSection } from '@/components/settings/iam/IamSection'
import { IntegrationsSection } from '@/components/settings/IntegrationsSection'
import { DiscordSection } from '@/components/settings/discord/DiscordSection'
import { ModerationSection } from '@/components/settings/ModerationSection'
import { AutoModSection } from '@/components/settings/automod/AutoModSection'
import { AutoInvitesSection } from '@/components/settings/AutoInvitesSection'
import { VRChatSection } from '@/components/settings/VRChatSection'
import { AiSection } from '@/components/settings/ai/AiSection'
import { ApiSection } from '@/components/settings/api/ApiSection'
import { VRChatProxySection } from '@/components/settings/proxy/VRChatProxySection'
import { PurgeSection } from '@/components/settings/PurgeSection'
import { PairedCompanionsSection } from '@/components/settings/PairedCompanionsSection'
import { api, ApiError, type CurrentUser, type OnboardingStatus } from '@/lib/api'
import { canAny } from '@/lib/permissions'
import { cn } from '@/lib/utils'

/**
 * The topics, in order, under the heading each is listed beneath. The id is what
 * `/settings#evidence` names, so a pasted link opens that topic. Adding a topic means one entry
 * here and one line in `Panel` below.
 *
 * The order is the owner's, not the build order: what the group runs on first, the install itself
 * near the end, and the developer tools last. Fourteen tabs in one row did not fit a desk and
 * showed three on a phone (settings review 2026-09-27 §1.1).
 *
 * `needs` is the permission a topic asks for. Most are a setting of the deployment and ask for
 * Manage settings; People and roles is Modbot's own accounts and roles, which are managed by their
 * own two permissions (accounts and access design §3, §4); Purge a person asks for Administrator,
 * the permission evidence storage design §14 already gives it. Paired companions opens for
 * somebody who may pair one (their own) or who manages users (everybody's).
 */
const TABS = [
  { value: 'moderation', label: 'Moderation', group: 'Your group', needs: ['ManageSettings'] },
  { value: 'automod', label: 'AutoMod', group: 'Your group', needs: ['ManageSettings'] },
  { value: 'auto-invites', label: 'Auto-invites', group: 'Your group', needs: ['ManageAutoInvites'] },
  { value: 'evidence', label: 'Evidence', group: 'Your group', needs: ['ManageSettings'] },
  { value: 'iam', label: 'People and roles', group: 'People', needs: ['ManageUsers', 'ManageRoles'] },
  { value: 'companions', label: 'Paired companions', group: 'People', needs: ['PairCompanion', 'ManageUsers'] },
  { value: 'purge', label: 'Purge a person', group: 'People', needs: ['Administrator'] },
  { value: 'vrchat', label: "Modbot's VRChat login", group: 'Connections', needs: ['ManageSettings'] },
  { value: 'discord', label: 'Discord', group: 'Connections', needs: ['ManageSettings'] },
  { value: 'integrations', label: 'Email and alerts', group: 'Connections', needs: ['ManageSettings'] },
  { value: 'ai', label: 'AI', group: 'Connections', needs: ['ManageSettings'] },
  { value: 'data', label: 'Server', group: 'This install', needs: ['ManageSettings'] },
  { value: 'api', label: 'API', group: 'For developers', needs: ['ManageSettings'] },
  { value: 'proxy', label: 'VRChat proxy', group: 'For developers', needs: ['ManageSettings'] },
] as const

type Tab = (typeof TABS)[number]
type TabId = Tab['value']

/**
 * The topic the hash names: the part before any `/`, so a topic with its own sub-tabs can use
 * `#ai/chat`. Null when the hash names none, which on a phone shows the list.
 */
function tabFromHash(open: readonly TabId[]): TabId | null {
  const hash = window.location.hash.slice(1)

  // AutoMod used to be a sub-tab of AI. A link to it from then opens the topic it became.
  if (hash === 'ai/moderation' || hash.startsWith('ai/moderation/')) {
    window.history.replaceState(window.history.state, '', '#automod')
    if (open.includes('automod')) return 'automod'
  }

  // Sync was a tab of its own until 2026-09-27. Its read-only timings are now on Server.
  if (hash === 'sync') {
    window.history.replaceState(window.history.state, '', '#data')
    if (open.includes('data')) return 'data'
  }

  const wanted = hash.split('/')[0]
  return open.find((t) => t === wanted) ?? null
}

/**
 * Settings.
 *
 * One topic at a time, picked from a list down the left, grouped under a few headings. On a phone
 * the list is its own screen: tap a topic to open it, and the name above it leads back.
 *
 * Three of the topics are the onboarding wizard's own steps, re-run in place. Spec 7.1 designed each
 * step as an independently re-runnable slice for exactly this reason, so a deployment whose host
 * became WAF-blocked re-runs the connection check rather than the wizard.
 *
 * The topics a person may not open are not listed at all, because Users and Roles moved in here and
 * their permissions are not Manage settings. The server refuses the data regardless.
 */
export function Settings({ me }: { me: CurrentUser }) {
  const open = useMemo(
    () => TABS.filter((t) => canAny(me, t.needs)).map((t) => t.value),
    [me],
  )

  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  // Kept apart from `status`, which is null both while it loads and after it failed, so the three
  // topics that wait for it can say which.
  const [statusError, setStatusError] = useState<string | null>(null)
  // Null is "no topic named": the list on a phone, the first topic at a desk.
  const [tab, setTab] = useState<TabId | null>(() => tabFromHash(open))

  const refresh = useCallback(
    () =>
      api
        .onboardingStatus()
        .then((next) => {
          setStatus(next)
          setStatusError(null)
        })
        .catch((e: unknown) => {
          setStatus(null)
          setStatusError(e instanceof ApiError ? e.message : 'Could not load settings.')
        }),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Back and forward, and a link to another topic clicked while already on this page. A pushed
  // entry comes back as popstate, a typed or clicked hash as hashchange.
  useEffect(() => {
    if (open.length === 0) return

    const onHash = () => setTab(tabFromHash(open))
    window.addEventListener('hashchange', onHash)
    window.addEventListener('popstate', onHash)
    return () => {
      window.removeEventListener('hashchange', onHash)
      window.removeEventListener('popstate', onHash)
    }
  }, [open])

  if (open.length === 0) return null

  const shown = tab ?? open[0]
  const listed = TABS.filter((t) => open.includes(t.value))

  const choose = (next: TabId) => {
    // From the list, pushed, so a phone's Back returns to the list. Between topics, replaced:
    // clicking through five topics should not take five Backs to leave.
    if (tab === null) window.history.pushState(window.history.state, '', `#${next}`)
    else window.history.replaceState(window.history.state, '', `#${next}`)
    setTab(next)
  }

  const toList = () => {
    window.history.replaceState(window.history.state, '', window.location.pathname)
    setTab(null)
  }

  return (
    <div className="grid w-full max-w-[112rem] items-start gap-5 lg:grid-cols-[minmax(12rem,max-content)_minmax(0,1fr)]">
      <TopicList
        tabs={listed}
        current={shown}
        onChoose={choose}
        className={cn(tab !== null && 'hidden lg:flex')}
      />

      <div className={cn('@container flex min-w-0 flex-col gap-3', tab === null && 'hidden lg:flex')}>
        <button
          type="button"
          onClick={toList}
          className="flex h-(--control-h) items-center gap-1 self-start font-medium text-foreground lg:hidden"
        >
          <ChevronLeft className="size-4 shrink-0 text-muted-foreground" aria-hidden />
          {listed.find((t) => t.value === shown)?.label}
        </button>
        <Panel tab={shown} me={me} status={status} statusError={statusError} refresh={refresh} />
      </div>
    </div>
  )
}

/**
 * The topics down the left, drawn like the sidebar's pages (console look §4.1): a heading with a
 * hairline, then entries a control high with a bar on the current one. On a phone it is the whole
 * screen and nothing is current, so each entry carries a chevron instead.
 *
 * At a desk the list is as wide as its longest name and never under 12rem. A fixed 12rem cut
 * "Modbot's VRChat login" short in Headset, where the words are bigger.
 */
function TopicList({
  tabs,
  current,
  onChoose,
  className,
}: {
  tabs: readonly Tab[]
  current: TabId
  onChoose: (next: TabId) => void
  className?: string
}) {
  return (
    <nav aria-label="Settings" className={cn('flex flex-col', className)}>
      {tabs.map((t, i) => {
        const lit = current === t.value
        return (
          <div key={t.value}>
            {(i === 0 || tabs[i - 1].group !== t.group) && (
              <div
                className={cn(
                  'flex items-center gap-2 pr-1 pb-1 font-label text-muted-foreground',
                  i > 0 && 'pt-4',
                )}
                style={{ fontSize: 'var(--text-small)' }}
              >
                {t.group}
                <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
              </div>
            )}
            <button
              type="button"
              onClick={() => onChoose(t.value)}
              aria-current={lit ? 'page' : undefined}
              className={cn(
                'relative flex h-(--control-h) w-full items-center gap-2 pr-2 pl-3 text-left transition-colors',
                lit
                  ? 'text-foreground lg:bg-card lg:font-medium'
                  : 'text-foreground hover:bg-card/60 lg:text-muted-foreground lg:hover:text-foreground',
              )}
            >
              {lit && <span aria-hidden className="absolute inset-y-0 left-0 hidden w-0.5 bg-primary lg:block" />}
              <span className="min-w-0 flex-1 truncate">{t.label}</span>
              <ChevronRight className="size-3.5 shrink-0 text-muted-foreground lg:hidden" aria-hidden />
            </button>
          </div>
        )
      })}
    </nav>
  )
}

function Panel({
  tab,
  me,
  status,
  statusError,
  refresh,
}: {
  tab: TabId
  me: CurrentUser
  status: OnboardingStatus | null
  statusError: string | null
  refresh: () => Promise<void>
}) {
  switch (tab) {
    case 'data':
      return <DataSection />
    case 'iam':
      return <IamSection me={me} />
    case 'vrchat':
      return <VRChatSection status={status} statusError={statusError} refresh={refresh} />
    case 'integrations':
      return <IntegrationsSection status={status} statusError={statusError} refresh={refresh} />
    case 'discord':
      return <DiscordSection status={status} statusError={statusError} refresh={refresh} />
    case 'moderation':
      return <ModerationSection />
    case 'automod':
      return <AutoModSection />
    case 'evidence':
      return <EvidenceSection />
    case 'ai':
      return <AiSection />
    case 'api':
      return <ApiSection />
    case 'proxy':
      return <VRChatProxySection />
    case 'auto-invites':
      return <AutoInvitesSection />
    case 'purge':
      return <PurgeSection />
    case 'companions':
      return <PairedCompanionsSection />
  }
}
