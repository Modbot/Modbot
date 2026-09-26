import { cn } from '@/lib/utils'
import { IS_MAC, keyNames } from '@/lib/shortcuts'

/**
 * A key, drawn as a key. `keys` is the registry's spelling: `mod+k`, `g m`, `?`.
 *
 * Sized from its own text, so it sits inside a button or a row of any height without setting it,
 * and hidden below `lg`: a phone has no keyboard to press it on.
 */
export function Kbd({ keys, className }: { keys: string; className?: string }) {
  return (
    <span className={cn('hidden items-center gap-1 lg:inline-flex', className)} style={{ fontSize: 'var(--text-tiny)' }}>
      {keyNames(keys, IS_MAC).map((name, i) => (
        <span key={i} className="contents">
          {i > 0 && <span className="text-muted-foreground">then</span>}
          <kbd className="inline-flex min-w-[1.5em] items-center justify-center rounded-sm border-(length:--hairline) bg-card px-1 font-mono leading-normal text-muted-foreground">
            {name}
          </kbd>
        </span>
      ))}
    </span>
  )
}
