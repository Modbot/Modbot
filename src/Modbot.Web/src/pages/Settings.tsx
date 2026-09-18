import { useCallback, useEffect, useState } from 'react'
import { DataSection } from '@/components/settings/DataSection'
import { EvidenceSection } from '@/components/settings/EvidenceSection'
import { IntegrationsSection } from '@/components/settings/IntegrationsSection'
import { DiscordSection } from '@/components/settings/discord/DiscordSection'
import { ModerationSection } from '@/components/settings/ModerationSection'
import { AutoModSection } from '@/components/settings/automod/AutoModSection'
import { SyncSection } from '@/components/settings/SyncSection'
import { VRChatSection } from '@/components/settings/VRChatSection'
import { AiSection } from '@/components/settings/ai/AiSection'
import { ApiSection } from '@/components/settings/api/ApiSection'
import { VRChatProxySection } from '@/components/settings/proxy/VRChatProxySection'
import { Tabs } from '@/components/ui/tabs'
import { api, type OnboardingStatus } from '@/lib/api'

/**
 * The tabs, in order. The id is what `/settings#evidence` names, so a pasted link opens that tab.
 * Adding a topic means one entry here and one line in `Panel` below.
 */
const TABS = [
  { value: 'data', label: 'Host & Database' },
  { value: 'vrchat', label: 'VRChat Service Account' },
  { value: 'integrations', label: 'Integrations' },
  { value: 'discord', label: 'Discord' },
  { value: 'moderation', label: 'Moderation' },
  { value: 'automod', label: 'AutoMod' },
  { value: 'evidence', label: 'Evidence' },
  { value: 'sync', label: 'Sync' },
  { value: 'ai', label: 'AI' },
  { value: 'api', label: 'API' },
  { value: 'proxy', label: 'VRChat Proxy' },
] as const

type TabId = (typeof TABS)[number]['value']

/** The part of the hash before any `/`, so a tab with its own sub-tabs can use `#ai/chat`. */
function tabFromHash(): TabId {
  const hash = window.location.hash.slice(1)

  // AutoMod used to be a sub-tab of AI. A link to it from then opens the tab it became.
  if (hash === 'ai/moderation' || hash.startsWith('ai/moderation/')) {
    window.history.replaceState(window.history.state, '', '#automod')
    return 'automod'
  }

  const wanted = hash.split('/')[0]
  return TABS.find((t) => t.value === wanted)?.value ?? TABS[0].value
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
 */
export function Settings() {
  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [tab, setTab] = useState<TabId>(tabFromHash)

  const refresh = useCallback(
    () => api.onboardingStatus().then(setStatus).catch(() => setStatus(null)),
    [],
  )

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Back and forward, and a link to another tab clicked while already on this page.
  useEffect(() => {
    const onHash = () => setTab(tabFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const choose = (next: TabId) => {
    setTab(next)
    // Replaced rather than pushed: clicking through five tabs should not take five Backs to leave.
    window.history.replaceState(window.history.state, '', `#${next}`)
  }

  return (
    <div className="w-full max-w-[112rem]">
      <Tabs value={tab} onChange={choose} tabs={[...TABS]} className="gap-5">
        <Panel tab={tab} status={status} refresh={refresh} />
      </Tabs>
    </div>
  )
}

function Panel({
  tab,
  status,
  refresh,
}: {
  tab: TabId
  status: OnboardingStatus | null
  refresh: () => Promise<void>
}) {
  switch (tab) {
    case 'data':
      return <DataSection />
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
  }
}
