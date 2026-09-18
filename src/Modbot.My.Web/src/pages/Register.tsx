import { useEffect, useState } from 'react'
import { AddInstance } from '@/components/AddInstance'
import { InstanceList } from '@/components/InstanceList'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { normaliseInstanceUrl } from '@/lib/instanceUrl'
import { askServer, type GroupDetails } from '@/lib/serverDetails'
import { recordUse } from '@/lib/storage'
import { useKnownInstances } from '@/lib/useKnownInstances'
import { usePendingSends } from '@/lib/useOutbox'

/**
 * Where a Modbot sends somebody to have its address remembered.
 *
 * `hints` is what the link claimed about the group. Anybody can write a link, so it is shown while
 * the page asks the Modbot itself and is replaced by whatever that answers; nothing about the group
 * is saved from the link (register details spec 2.1). my.modbot.co asks the same question
 * server-side before it saves anything.
 */
export function Register({ url, hints }: { url: string | null; hints: GroupDetails }) {
  // Saved in localStorage before the list below first reads it. App sends the server its copy.
  const [origin] = useState(() => {
    const normalised = normaliseInstanceUrl(url)
    if (normalised) recordUse(normalised, '/', 'register')
    return normalised
  })

  const [group, setGroup] = useState(hints)

  useEffect(() => {
    if (!origin) return

    const stop = new AbortController()

    // A Modbot that does not answer leaves the link's own hints on screen: they are a name and a
    // picture, they are saved nowhere, and an empty card would be worse for a server that is simply
    // slow.
    askServer(origin, stop.signal).then((answer) => {
      if (answer) setGroup(answer)
    })

    return () => stop.abort()
  }, [origin])

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
      <Card className="gap-4 overflow-hidden px-0 pt-0">
        {group.bannerUrl && (
          <img
            src={group.bannerUrl}
            alt=""
            className="h-24 w-full object-cover sm:h-32"
            referrerPolicy="no-referrer"
          />
        )}
        <div className="flex flex-col gap-4 px-6">
          <h1 className="text-base font-display">Instance saved</h1>
          <div className="flex items-center gap-3">
            {group.iconUrl && (
              <img
                src={group.iconUrl}
                alt=""
                className="size-10 shrink-0 rounded-md object-cover"
                referrerPolicy="no-referrer"
              />
            )}
            <div className="flex min-w-0 flex-col gap-1">
              {group.name && <p className="truncate font-medium">{group.name}</p>}
              <p className="break-all font-mono text-muted-foreground">{origin}</p>
            </div>
          </div>
          {waiting && (
            <p role="status" className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Saved on this device. Sending when my.modbot.co can be reached.
            </p>
          )}
          <div>
            <Button asChild>
              <a href={origin}>Back to instance</a>
            </Button>
          </div>
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
