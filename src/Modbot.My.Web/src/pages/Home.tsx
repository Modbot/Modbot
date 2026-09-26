import { AddServer } from '@/components/AddServer'
import { ServerList } from '@/components/ServerList'
import { Shell } from '@/components/Shell'
import { Card } from '@/components/ui/card'
import { recordUse } from '@/lib/storage'
import { useKnownServers } from '@/lib/useKnownServers'

export function Home() {
  const known = useKnownServers()

  return (
    <Shell>
      <Card className="gap-0 overflow-hidden py-0">
        <h1 className="border-b px-6 py-4 text-base font-display">Your servers</h1>
        {known.loaded || known.servers.length > 0 ? (
          <ServerList
            servers={known.servers}
            onOpen={(url) => recordUse(url, '/', 'open')}
            onRemove={known.remove}
          />
        ) : (
          <div className="px-6 py-8 text-center text-muted-foreground">Loading</div>
        )}
        <AddServer label="Add" onAdd={known.add} />
      </Card>
    </Shell>
  )
}
