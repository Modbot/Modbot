import { cn } from '@/lib/utils'

/**
 * Which system a fact came from, drawn the way the app draws it: the same labels and the same fixed
 * colours (src/Modbot.Web/src/components/facts.tsx). On this page it marks where each feature's
 * information comes from.
 */
export type Source = 'VRChat' | 'Sync' | 'Client' | 'Discord' | 'Modbot'

const SERIES: Record<Source, number> = { VRChat: 1, Sync: 3, Client: 4, Discord: 5, Modbot: 2 }

export function SourceBadge({ source, className }: { source: Source; className?: string }) {
  return (
    <span
      className={cn('inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5 leading-tight', className)}
      style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
    >
      <span className="size-1.5 shrink-0 rounded-full" style={{ background: `var(--series-${SERIES[source]})` }} />
      {source}
    </span>
  )
}
