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
      {/*
        The row scrolls sideways rather than wrapping or shrinking. Settings has twelve tabs, and
        a dozen labels squeezed into a phone's width are twelve unreadable words; four readable
        ones and a swipe is the trade. `whitespace-nowrap` keeps a label on one line, and the
        thin scrollbar stays out of the way on a mouse.
      */}
      {/* The line under the row belongs to the wrapper, not to the scrolling row: a scrolling box
          clips both axes, and an underline drawn one pixel below a tab would be cut off. */}
      <div className="shrink-0 border-b-(length:--hairline)">
        <div
          role="tablist"
          className="flex items-stretch overflow-x-auto [scrollbar-width:thin]"
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
                'relative flex shrink-0 items-center px-3 py-2 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
                value === tab.value
                  ? 'text-foreground'
                  : 'text-muted-foreground hover:text-foreground',
              )}
              style={{ fontSize: 'var(--text-small)', minHeight: 'var(--control-h)' }}
            >
              {tab.label}
              {typeof tab.badge === 'number' && tab.badge > 0 && (
                <span className="ml-1.5 font-mono text-muted-foreground">{tab.badge}</span>
              )}
              {value === tab.value && (
                <span className="absolute inset-x-0 bottom-0 h-0.5 bg-primary" />
              )}
            </button>
          ))}
        </div>
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
