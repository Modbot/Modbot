import { useEffect, useState } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { ApiKeysPanel } from './ApiKeysPanel'
import { WebhooksPanel } from './WebhooksPanel'
import { EventsPanel } from './EventsPanel'

/**
 * The API sub-tabs, in order. The id is the part after the slash in `/settings#api/keys`.
 * The reasoning behind all three is in .agent/specs/2026-09-15-api-keys-websocket-webhooks-design.md
 * and the guide for people integrating is docs/content/docs/api/; the screens carry labels only.
 */
const API_TABS = [
  { value: 'keys', label: 'Keys', panel: ApiKeysPanel },
  { value: 'webhooks', label: 'Webhooks', panel: WebhooksPanel },
  { value: 'events', label: 'Events', panel: EventsPanel },
] as const

type ApiTabId = (typeof API_TABS)[number]['value']

function subTabFromHash(): ApiTabId {
  const asked = window.location.hash.slice(1).split('/')[1]
  // The sub-tab was called WebSocket before long polling joined it; old links still land on it.
  const wanted = asked === 'websocket' ? 'events' : asked
  return API_TABS.find((t) => t.value === wanted)?.value ?? API_TABS[0].value
}

/** Settings → API: its own row of tabs under the settings tabs. */
export function ApiSection() {
  const [tab, setTab] = useState<ApiTabId>(subTabFromHash)

  useEffect(() => {
    const onHash = () => setTab(subTabFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const choose = (next: ApiTabId) => {
    setTab(next)
    window.history.replaceState(window.history.state, '', `#api/${next}`)
  }

  return (
    <Tabs
      value={tab}
      onChange={choose}
      tabs={API_TABS.map(({ value, label }) => ({ value, label }))}
      className="gap-5"
    >
      {API_TABS.map(({ value, panel: Panel }) => (value === tab ? <Panel key={value} /> : null))}
    </Tabs>
  )
}
