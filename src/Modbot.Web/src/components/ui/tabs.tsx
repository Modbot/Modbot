import { useId } from 'react'
import { cn } from '@/lib/utils'

/**
 * A row of tabs and the panel under it.
 *
 * Hand-written rather than another Radix primitive, because what the popup needs is three buttons
 * and one panel: the arrow-key roving the full primitive adds is real, and it is twenty lines
 * here against a dependency, a bundle and a second set of styling conventions.
 *
 * The chosen tab is held by the caller, so a popup can put it in its own state and a screen that
 * wants it in the URL later can do that without changing this.
 */
export function Tabs<T extends string>({
  value,
  onChange,
  tabs,
  children,
  className,
}: {
  value: T
  onChange: (next: T) => void
  tabs: { value: T; label: string; badge?: number | null }[]
  children: React.ReactNode
  className?: string
}) {
  const id = useId()

  return (
    <div className={cn('flex min-h-0 flex-col', className)}>
      <div
        role="tablist"
        className="flex shrink-0 items-center gap-1 border-b px-1"
        style={{ borderBottomWidth: 'var(--hairline)' }}
      >
        {tabs.map((tab) => (
          <button
            key={tab.value}
            type="button"
            role="tab"
            id={`${id}-${tab.value}`}
            aria-selected={value === tab.value}
            aria-controls={`${id}-panel`}
            onClick={() => onChange(tab.value)}
            className={cn(
              'relative rounded-t px-3 py-2 font-medium transition-colors focus-visible:outline-2 focus-visible:outline-ring',
              value === tab.value
                ? 'text-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
            style={{ fontSize: 'var(--text-small)' }}
          >
            {tab.label}
            {typeof tab.badge === 'number' && tab.badge > 0 && (
              <span className="ml-1.5 tabular-nums text-muted-foreground">{tab.badge}</span>
            )}
            {value === tab.value && (
              <span className="absolute inset-x-1 -bottom-px h-0.5 rounded-full bg-foreground" />
            )}
          </button>
        ))}
      </div>

      <div
        role="tabpanel"
        id={`${id}-panel`}
        aria-labelledby={`${id}-${value}`}
        className="min-h-0 flex-1 overflow-auto"
      >
        {children}
      </div>
    </div>
  )
}
