import { AddInstance } from '@/components/AddInstance'
import { InstanceList } from '@/components/InstanceList'
import { Shell } from '@/components/Shell'
import { Card } from '@/components/ui/card'
import { recordUse } from '@/lib/storage'
import { useKnownInstances } from '@/lib/useKnownInstances'

export function Home() {
  const known = useKnownInstances()

  return (
    <Shell>
      <Card className="gap-0 overflow-hidden py-0">
        <h1 className="border-b px-6 py-4 text-base font-semibold">Your instances</h1>
        {known.loaded || known.instances.length > 0 ? (
          <InstanceList
            instances={known.instances}
            onOpen={(url) => recordUse(url, '/', 'open')}
            onRemove={known.remove}
          />
        ) : (
          <div className="px-6 py-8 text-center text-muted-foreground">Loading</div>
        )}
        <AddInstance label="Add" onAdd={known.add} />
      </Card>
    </Shell>
  )
}
