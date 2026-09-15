import { useEffect, useState } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { AiAlertsSettings } from './AiAlertsSettings'
import { AiBaseSettings } from './AiBaseSettings'
import { AiInsightsSettings } from './AiInsightsSettings'
import { AiLimitsSettings } from './AiLimitsSettings'
import { AiChatSettings } from './AiChatSettings'
import { AiModerationSettings } from './AiModerationSettings'

/**
 * The AI sub-tabs, in order. The id is the part after the slash in `/settings#ai/base`.
 *
 * Adding a sub-tab is one entry here and one panel component in this folder. Every AI feature
 * gets its client from the settings on Base, so Base stays first.
 */
const AI_TABS = [
  { value: 'base', label: 'Base', panel: AiBaseSettings },
  { value: 'insights', label: 'Insights', panel: AiInsightsSettings },
  { value: 'alerts', label: 'Alerts', panel: AiAlertsSettings },
  { value: 'chat', label: 'Chat', panel: AiChatSettings },
  { value: 'moderation', label: 'Moderation', panel: AiModerationSettings },
  { value: 'limits', label: 'Limits', panel: AiLimitsSettings },
] as const

type AiTabId = (typeof AI_TABS)[number]['value']

function subTabFromHash(): AiTabId {
  const wanted = window.location.hash.slice(1).split('/')[1]
  return AI_TABS.find((t) => t.value === wanted)?.value ?? AI_TABS[0].value
}

/** Settings → AI: its own row of tabs under the settings tabs. */
export function AiSection() {
  const [tab, setTab] = useState<AiTabId>(subTabFromHash)

  useEffect(() => {
    const onHash = () => setTab(subTabFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const choose = (next: AiTabId) => {
    setTab(next)
    // Replaced rather than pushed, like the settings tabs above it.
    window.history.replaceState(window.history.state, '', `#ai/${next}`)
  }

  return (
    <Tabs
      value={tab}
      onChange={choose}
      tabs={AI_TABS.map(({ value, label }) => ({ value, label }))}
      className="gap-5"
    >
      {AI_TABS.map(({ value, panel: Panel }) => (value === tab ? <Panel key={value} /> : null))}
    </Tabs>
  )
}
