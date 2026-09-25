import { cn } from '@/lib/utils'
import { IS_MAC, describeKeys } from '@/lib/shortcuts'

/** A key, drawn as a key. `keys` is the registry's spelling: `mod+k`, `g m`, `?`. */
export function Kbd({ keys, className }: { keys: string; className?: string }) {
  return (
    <span className={cn('inline-flex items-center gap-1', className)}>
      {keys.split(' ').map((combo, i) => (
        <span key={i} className="contents">
          {i > 0 && <span className="text-muted-foreground/70" style={{ fontSize: '0.6875rem' }}>then</span>}
          <kbd
            className="inline-flex h-[calc(var(--control-h)-0.625rem)] min-w-[calc(var(--control-h)-0.625rem)] items-center justify-center rounded-sm border-(length:--hairline) bg-card px-1 font-mono text-muted-foreground"
            style={{ fontSize: '0.6875rem' }}
          >
            {describeKeys(combo, IS_MAC)}
          </kbd>
        </span>
      ))}
    </span>
  )
}
