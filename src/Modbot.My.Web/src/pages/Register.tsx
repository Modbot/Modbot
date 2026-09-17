import { useState } from 'react'
import { AddInstance } from '@/components/AddInstance'
import { InstanceList } from '@/components/InstanceList'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { normaliseInstanceUrl } from '@/lib/instanceUrl'
import { recordUse } from '@/lib/storage'
import { useKnownInstances } from '@/lib/useKnownInstances'
import { usePendingSends } from '@/lib/useOutbox'

export function Register({ url }: { url: string | null }) {
  // Saved in localStorage before the list below first reads it. App sends the server its copy.
  const [origin] = useState(() => {
    const normalised = normaliseInstanceUrl(url)
    if (normalised) recordUse(normalised, '/', 'register')
    return normalised
  })

  const known = useKnownInstances()

  // Shown once a send has failed, and until one succeeds. A send that just works shows nothing,
  // and the heading never says more than the browser has done for itself.
  const waiting = usePendingSends().some((e) => e.url === origin && e.tries > 0)

  if (!origin) {
    return (
      <Shell>
        <Card className="gap-4 px-6">
          <h1 className="text-base font-display">Invalid link</h1>
          <div>
            <Button asChild variant="outline">
              <Link href="/">Your instances</Link>
            </Button>
          </div>
        </Card>
      </Shell>
    )
  }

  return (
    <Shell>
      <Card className="gap-4 px-6">
        <div className="flex flex-col gap-1">
          <h1 className="text-base font-display">Instance saved</h1>
          <p className="break-all font-mono text-muted-foreground">{origin}</p>
          {waiting && (
            <p role="status" className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Saved on this device. Sending when my.modbot.co can be reached.
            </p>
          )}
        </div>
        <div>
          <Button asChild>
            <a href={origin}>Back to instance</a>
          </Button>
        </div>
      </Card>

      <Card className="gap-0 overflow-hidden py-0">
        <h2 className="border-b px-6 py-4 text-base font-display">Your instances</h2>
        <InstanceList
          instances={known.instances}
          onOpen={(u) => recordUse(u, '/', 'open')}
          onRemove={known.remove}
        />
        <AddInstance label="Add" onAdd={known.add} />
      </Card>
    </Shell>
  )
}
