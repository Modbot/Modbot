import { useState } from 'react'
import { Sidebar, Topbar, type PageId } from '@/components/Chrome'
import { usePreferences } from '@/lib/preferences'
import { Members } from '@/pages/Members'
import { Placeholder } from '@/pages/Placeholder'

const TITLES: Record<PageId, { title: string; subtitle?: string }> = {
  members: { title: 'Members', subtitle: 'Synced 40 seconds ago' },
  bans: { title: 'Bans', subtitle: 'Synced 2 minutes ago' },
  audit: { title: 'Audit log', subtitle: 'Live — 1 minute behind' },
  metrics: { title: 'Metrics', subtitle: 'Rollups rebuilt 04:00' },
  settings: { title: 'Settings' },
}

export default function App() {
  const [page, setPage] = useState<PageId>('members')
  const prefs = usePreferences()
  const { title, subtitle } = TITLES[page]

  return (
    <div className="grid h-screen grid-cols-[13.5rem_1fr]">
      <Sidebar page={page} onNavigate={setPage} />
      <main className="flex flex-col overflow-auto">
        <Topbar title={title} subtitle={subtitle} {...prefs} />
        <div className="p-5">
          {page === 'members' ? <Members /> : <Placeholder name={title} />}
        </div>
      </main>
    </div>
  )
}
