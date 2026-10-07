import { useState } from 'react'
import { MineTab } from '@/components/availability/MineTab'
import { TeamTab } from '@/components/availability/TeamTab'
import { Tabs } from '@/components/ui/tabs'
import type { CurrentUser } from '@/lib/api'
import { can } from '@/lib/permissions'

type Tab = 'team' | 'mine'

/**
 * Availability: when the team is free each week. Team is the heatmap, for somebody who may see
 * availability; Mine is the person's own week, for somebody who may enter it. A person holding
 * only one of the two sees only that tab. The server refuses the other whatever is shown here.
 */
export function Availability({ me }: { me: CurrentUser }) {
  const tabs: { value: Tab; label: string }[] = [
    ...(can(me, 'ViewAvailability') ? [{ value: 'team' as const, label: 'Team' }] : []),
    ...(can(me, 'EnterAvailability') ? [{ value: 'mine' as const, label: 'Mine' }] : []),
  ]

  const [tab, setTab] = useState<Tab>(tabs[0]?.value ?? 'team')
  const shown = tabs.some((t) => t.value === tab) ? tab : (tabs[0]?.value ?? 'team')

  return (
    <Tabs value={shown} onChange={setTab} tabs={tabs} panelClassName="overflow-visible pt-3">
      {shown === 'team' ? <TeamTab /> : <MineTab />}
    </Tabs>
  )
}
