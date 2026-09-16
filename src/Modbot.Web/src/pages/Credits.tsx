import { useEffect, useState } from 'react'
import { General } from '@/components/credits/General'
import { Libraries } from '@/components/credits/Libraries'
import { Services } from '@/components/credits/Services'
import { Tabs } from '@/components/ui/tabs'

/**
 * Credits: who made Modbot, the services it is built on, and every package it ships.
 *
 * Open to everyone signed in, whatever their role: nothing here is about the group. It lives at
 * `/settings/credits` because that is where the tabs are, and the address is not what decides who
 * may read it -- the Settings page's own entry in `lib/nav.ts` carries that permission, and this
 * page has none.
 *
 * The tabs, in order. The id is what `/settings/credits#libraries` names, so a pasted link opens
 * that tab. Adding a topic means one entry here and one line in `Panel` below.
 */
const TABS = [
  { value: 'general', label: 'General' },
  { value: 'libraries', label: 'Libraries' },
  { value: 'services', label: 'Services' },
] as const

type TabId = (typeof TABS)[number]['value']

/** The part of the hash before any `/`, so a tab with its own sub-tabs can use `#libraries/npm`. */
function tabFromHash(): TabId {
  const wanted = window.location.hash.slice(1).split('/')[0]
  return TABS.find((t) => t.value === wanted)?.value ?? TABS[0].value
}

export function Credits() {
  const [tab, setTab] = useState<TabId>(tabFromHash)

  // Back and forward, and a link to another tab clicked while already on this page.
  useEffect(() => {
    const onHash = () => setTab(tabFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const choose = (next: TabId) => {
    setTab(next)
    // Replaced rather than pushed: clicking through three tabs should not take three Backs to leave.
    window.history.replaceState(window.history.state, '', `#${next}`)
  }

  return (
    <div className="w-full max-w-5xl">
      <Tabs value={tab} onChange={choose} tabs={[...TABS]} className="gap-5">
        <Panel tab={tab} />
      </Tabs>
    </div>
  )
}

function Panel({ tab }: { tab: TabId }) {
  switch (tab) {
    case 'general':
      return <General />
    case 'libraries':
      return <Libraries />
    case 'services':
      return <Services />
  }
}
