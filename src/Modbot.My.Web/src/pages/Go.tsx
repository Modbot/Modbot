import { AddInstance } from '@/components/AddInstance'
import { InstanceList } from '@/components/InstanceList'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { safePath } from '@/lib/safePath'
import { recordUse } from '@/lib/storage'
import { useKnownInstances } from '@/lib/useKnownInstances'

/**
 * `/go?redir=<path>`: opens `<path>` on an instance the person picks from the list. It never picks
 * for them, even when only one instance is known: on a shared IP address, that instance may be one
 * somebody else opened.
 */
export function Go({ redir }: { redir: string | null }) {
  const path = safePath(redir)
  const known = useKnownInstances()

  if (path === null) {
    return (
      <Shell>
        <Card className="gap-4 px-6">
          <div className="flex flex-col gap-1">
            <h1 className="text-base font-semibold">Link refused</h1>
            <p className="break-all font-mono text-muted-foreground">{redir}</p>
          </div>
          <div>
            <Button asChild variant="outline">
              <Link href="/">Your instances</Link>
            </Button>
          </div>
        </Card>
      </Shell>
    )
  }

  if (!known.loaded) {
    return (
      <Shell>
        <Card className="gap-1 px-6">
          <h1 className="text-base font-semibold">Loading</h1>
        </Card>
      </Shell>
    )
  }

  const open = (url: string) => recordUse(url, path, 'go')

  return (
    <Shell>
      <Card className="gap-0 overflow-hidden py-0">
        <div className="flex flex-col gap-1 border-b px-6 py-4">
          <h1 className="text-base font-semibold">Choose an instance</h1>
          <p className="break-all font-mono text-muted-foreground">{path}</p>
        </div>
        <InstanceList instances={known.instances} path={path} onOpen={open} onRemove={known.remove} />
        <AddInstance
          label="Add and open"
          onAdd={(url) => {
            open(url)
            window.location.assign(url + path)
          }}
        />
      </Card>
    </Shell>
  )
}
