import { Button } from '@/components/ui/button'
import { ago, host } from '@/lib/format'
import type { KnownInstance } from '@/lib/merge'

/**
 * The combined instance list. Each entry opens `<instance><path>`.
 */
export function InstanceList({
  instances,
  path = '',
  onOpen,
  onRemove,
}: {
  instances: KnownInstance[]
  path?: string
  onOpen: (url: string) => void
  onRemove?: (url: string) => void
}) {
  if (instances.length === 0) {
    return <div className="px-6 py-8 text-center text-muted-foreground">No instances</div>
  }

  return (
    <ul className="flex flex-col" aria-label="Instances">
      {instances.map((instance) => (
        <li key={instance.url} className="flex items-center gap-2 border-t px-3 first:border-t-0">
          <a
            href={instance.url + path}
            onClick={() => onOpen(instance.url)}
            className="flex min-w-0 flex-1 flex-col rounded-md px-3 py-2.5 hover:bg-secondary"
          >
            <span className="flex items-baseline justify-between gap-3">
              <span className="truncate font-medium">{instance.name ?? host(instance.url)}</span>
              <span className="shrink-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {ago(instance.lastUsedAt)}
              </span>
            </span>
            <span className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {instance.url + path}
            </span>
          </a>
          {onRemove && (
            <Button variant="ghost" size="sm" onClick={() => onRemove(instance.url)}>
              Remove
            </Button>
          )}
        </li>
      ))}
    </ul>
  )
}
