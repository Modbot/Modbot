import { cn } from '@/lib/utils'
import { IS_MAC, keyNames } from '@/lib/shortcuts'

/**
 * A key, drawn as a key. `keys` is the registry's spelling: `mod+k`, `g m`, `?`.
 *
 * Sized from its own text, so it sits inside a button or a row of any height without setting it,
 * and hidden below `lg` and in a headset: neither has a keyboard to hand.
 *
 * `compact` drops the "then" between a sequence's keys, for the sidebar, where every row's hint is
 * the same two-key shape and the word would only crowd the labels.
 */
export function Kbd({ keys, className, compact = false }: { keys: string; className?: string; compact?: boolean }) {
  return (
    <span
      className={cn('hidden items-center desk:lg:inline-flex', compact ? 'gap-0.5' : 'gap-1', className)}
      style={{ fontSize: 'var(--text-tiny)' }}
    >
      {keyNames(keys, IS_MAC).map((name, i) => (
        <span key={i} className="contents">
          {i > 0 && !compact && <span className="text-muted-foreground">then</span>}
          <kbd className="inline-flex min-w-[1.5em] items-center justify-center rounded-sm border-(length:--hairline) bg-card px-1 font-mono leading-normal text-muted-foreground">
            {name}
          </kbd>
        </span>
      ))}
    </span>
  )
}
