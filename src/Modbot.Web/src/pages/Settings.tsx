import { useCallback, useEffect, useMemo, useState } from 'react'
import { DataSection } from '@/components/settings/DataSection'
import { EvidenceSection } from '@/components/settings/EvidenceSection'
import { IamSection } from '@/components/settings/iam/IamSection'
import { IntegrationsSection } from '@/components/settings/IntegrationsSection'
import { DiscordSection } from '@/components/settings/discord/DiscordSection'
import { ModerationSection } from '@/components/settings/ModerationSection'
import { AutoModSection } from '@/components/settings/automod/AutoModSection'
import { SyncSection } from '@/components/settings/SyncSection'
import { VRChatSection } from '@/components/settings/VRChatSection'
import { AiSection } from '@/components/settings/ai/AiSection'
import { ApiSection } from '@/components/settings/api/ApiSection'
import { VRChatProxySection } from '@/components/settings/proxy/VRChatProxySection'
import { PurgeSection } from '@/components/settings/PurgeSection'
import { Tabs } from '@/components/ui/tabs'
import { api, type CurrentUser, type OnboardingStatus } from '@/lib/api'
import { canAny } from '@/lib/permissions'

/**
 * The tabs, in order. The id is what `/settings#evidence` names, so a pasted link opens that tab.
 * Adding a topic means one entry here and one line in `Panel` below.
 *
 * `needs` is the permission a tab asks for. Most tabs are a setting of the deployment and ask for
 * Manage settings; IAM is Modbot's own accounts and roles, which are managed by their own two
 * permissions (accounts and access design §3, §4); Purge a person asks for Administrator, the
 * permission evidence storage design §14 already gives it.
 */
const TABS = [
  { value: 'data', label: 'Host & Database', needs: ['ManageSettings'] },
  { value: 'iam', label: 'IAM', needs: ['ManageUsers', 'ManageRoles'] },
  { value: 'vrchat', label: 'VRChat Service Account', needs: ['ManageSettings'] },
  { value: 'integrations', label: 'Integrations', needs: ['ManageSettings'] },
  { value: 'discord', label: 'Discord', needs: ['ManageSettings'] },
  { value: 'moderation', label: 'Moderation', needs: ['ManageSettings'] },
  { value: 'automod', label: 'AutoMod', needs: ['ManageSettings'] },
  { value: 'evidence', label: 'Evidence', needs: ['ManageSettings'] },
  { value: 'sync', label: 'Sync', needs: ['ManageSettings'] },
  { value: 'ai', label: 'AI', needs: ['ManageSettings'] },
  { value: 'api', label: 'API', needs: ['ManageSettings'] },
  { value: 'proxy', label: 'VRChat Proxy', needs: ['ManageSettings'] },
  { value: 'purge', label: 'Purge a person', needs: ['Administrator'] },
] as const

type TabId = (typeof TABS)[number]['value']

/** The part of the hash before any `/`, so a tab with its own sub-tabs can use `#ai/chat`. */
function tabFromHash(open: readonly TabId[]): TabId {
  const hash = window.location.hash.slice(1)

  // AutoMod used to be a sub-tab of AI. A link to it from then opens the tab it became.
  if (hash === 'ai/moderation' || hash.startsWith('ai/moderation/')) {
    window.history.replaceState(window.history.state, '', '#automod')
    if (open.includes('automod')) return 'automod'
  }

  const wanted = hash.split('/')[0]
  return open.find((t) => t === wanted) ?? open[0]
}

/**
 * Settings.
 *
 * One topic at a time, picked from a row of tabs across the top. The page used to be every section
 * end to end with a scrolling nav beside it; the maintainer asked for tabs, so an operator sees
 * only the settings they came for.
 *
 * Three of the tabs are the onboarding wizard's own steps, re-run in place. Spec 7.1 designed each
 * step as an independently re-runnable slice for exactly this reason, so a deployment whose host
 * became WAF-blocked re-runs the connection check rather than the wizard.
 *
 * The tabs a person may not open are not drawn at all, because Users and Roles moved in here and
 * their permissions are not Manage settings. The server refuses the data regardless.
 */
export function Settings({ me }: { me: CurrentUser }) {
  const open = useMemo(
    () => TABS.filter((t) => canAny(me, t.needs)).map((t) => t.value),
    [me],
  )

  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [tab, setTab] = useState<TabId | null>(() => (open.length === 0 ? null : tabFromHash(open)))

  const refresh = useCallback(
    () => api.onboardingStatus().then(setStatus).catch(() => setStatus(null)),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Back and forward, and a link to another tab clicked while already on this page.
  useEffect(() => {
    if (open.length === 0) return

    const onHash = () => setTab(tabFromHash(open))
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [open])

  if (tab === null) return null

  const choose = (next: TabId) => {
    setTab(next)
    // Replaced rather than pushed: clicking through five tabs should not take five Backs to leave.
    window.history.replaceState(window.history.state, '', `#${next}`)
  }

  return (
    <div className="w-full max-w-[112rem]">
      <Tabs
        value={tab}
        onChange={choose}
        tabs={TABS.filter((t) => open.includes(t.value)).map(({ value, label }) => ({ value, label }))}
        className="gap-5"
      >
        <Panel tab={tab} me={me} status={status} refresh={refresh} />
      </Tabs>
    </div>
  )
}

function Panel({
  tab,
  me,
  status,
  refresh,
}: {
  tab: TabId
  me: CurrentUser
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  switch (tab) {
    case 'data':
      return <DataSection />
    case 'iam':
      return <IamSection me={me} />
    case 'vrchat':
      return <VRChatSection status={status} refresh={refresh} />
    case 'integrations':
      return <IntegrationsSection status={status} refresh={refresh} />
    case 'discord':
      return <DiscordSection status={status} refresh={refresh} />
    case 'moderation':
      return <ModerationSection />
    case 'automod':
      return <AutoModSection />
    case 'evidence':
      return <EvidenceSection />
    case 'sync':
      return <SyncSection />
    case 'ai':
      return <AiSection />
    case 'api':
      return <ApiSection />
    case 'proxy':
      return <VRChatProxySection />
    case 'purge':
      return <PurgeSection />
  }
}
