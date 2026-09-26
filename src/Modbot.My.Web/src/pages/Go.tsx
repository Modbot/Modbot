import { AddServer } from '@/components/AddServer'
import { Link } from '@/components/Link'
import { ServerList } from '@/components/ServerList'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { safePath } from '@/lib/safePath'
import { recordUse } from '@/lib/storage'
import { useKnownServers } from '@/lib/useKnownServers'

/**
 * `/go?redir=<path>`: opens `<path>` on a server the person picks from the list. It never picks
 * for them, even when only one server is known: on a shared IP address, that server may be one
 * somebody else opened.
 */
export function Go({ redir }: { redir: string | null }) {
  const path = safePath(redir)
  const known = useKnownServers()

  if (path === null) {
    return (
      <Shell>
        <Card className="gap-4 px-6">
          <div className="flex flex-col gap-1">
            <h1 className="text-base font-display">Link refused</h1>
            <p className="break-all font-mono text-muted-foreground">{redir}</p>
          </div>
          <div>
            <Button asChild variant="outline">
              <Link href="/">Your servers</Link>
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
          <h1 className="text-base font-display">Loading</h1>
        </Card>
      </Shell>
    )
  }

  const open = (url: string) => recordUse(url, path, 'go')

  return (
    <Shell>
      <Card className="gap-0 overflow-hidden py-0">
        <div className="flex flex-col gap-1 border-b px-6 py-4">
          <h1 className="text-base font-display">Choose a server</h1>
          <p className="break-all font-mono text-muted-foreground">{path}</p>
        </div>
        <ServerList servers={known.servers} path={path} onOpen={open} onRemove={known.remove} />
        <AddServer
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
