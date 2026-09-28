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
  wrap = false,
  rowClassName,
  panelClassName,
}: {
  value: T
  onChange: (next: T) => void
  tabs: { value: T; label: string; badge?: number | null }[]
  children: React.ReactNode
  className?: string
  /** Every tab in sight on more than one line, rather than a row that scrolls sideways. */
  wrap?: boolean
  /** For the row of tabs, such as pinning it while the page scrolls under it. */
  rowClassName?: string
  /** For the panel, such as letting it grow with the page rather than scroll on its own. */
  panelClassName?: string
}) {
  const id = useId()

  return (
    <div className={cn('flex min-h-0 flex-col', className)}>
      {/*
        The row scrolls sideways rather than shrinking. AI in Settings has seven tabs, and
        seven labels squeezed into a phone's width are seven unreadable words; four readable
        ones and a swipe is the trade. `whitespace-nowrap` keeps a label on one line, and the
        thin scrollbar stays out of the way on a mouse. A swipe is still the last resort, so tabs
        sit closer together below `sm`: the person popup's six tabs fit a 390px phone at 8px a
        side and do not at 12px. `wrap` puts the rest on a second line instead, for a screen
        where a tab past the edge is a tab nobody finds.
      */}
      {/* The line under the row belongs to the wrapper, not to the scrolling row: a scrolling box
          clips both axes, and an underline drawn one pixel below a tab would be cut off. */}
      <div className={cn('shrink-0 border-b-(length:--hairline)', rowClassName)}>
        <div
          role="tablist"
          className={cn('flex items-stretch', wrap ? 'flex-wrap' : 'overflow-x-auto [scrollbar-width:thin]')}
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
                'relative flex shrink-0 items-center px-2 py-2 sm:px-3 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
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
        className={cn('min-h-0 flex-1 overflow-auto', panelClassName)}
      >
        {children}
      </div>
    </div>
  )
}
