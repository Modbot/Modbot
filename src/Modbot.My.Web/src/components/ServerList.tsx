import { Button } from '@/components/ui/button'
import { ago, host } from '@/lib/format'
import type { KnownServer } from '@/lib/merge'

/**
 * The combined server list. Each entry opens `<server><path>`.
 */
export function ServerList({
  servers,
  path = '',
  onOpen,
  onRemove,
}: {
  servers: KnownServer[]
  path?: string
  onOpen: (url: string) => void
  onRemove?: (url: string) => void
}) {
  if (servers.length === 0) {
    return <div className="px-6 py-8 text-center text-muted-foreground">No servers</div>
  }

  return (
    <ul className="flex flex-col" aria-label="Servers">
      {servers.map((server) => (
        <li key={server.url} className="flex items-center gap-2 border-t px-3 first:border-t-0">
          <a
            href={server.url + path}
            onClick={() => onOpen(server.url)}
            className="flex min-w-0 flex-1 flex-col rounded-md px-3 py-2.5 hover:bg-secondary"
          >
            <span className="flex items-baseline justify-between gap-3">
              <span className="truncate font-medium">{server.name ?? host(server.url)}</span>
              <span className="shrink-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {ago(server.lastUsedAt)}
              </span>
            </span>
            <span className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {server.url + path}
            </span>
          </a>
          {onRemove && (
            <Button variant="ghost" size="sm" onClick={() => onRemove(server.url)}>
              Remove
            </Button>
          )}
        </li>
      ))}
    </ul>
  )
}
