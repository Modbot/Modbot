import { cn } from '@/lib/utils'

/**
 * A choice of two to five values, drawn as one bordered box with a hairline between the segments
 * and the chosen one filled with the accent: density, a date range, Day/Week/Month, a list's two
 * views.
 *
 * Tabs are for switching a page's sections; this is for switching how one thing is shown. The
 * height is the control height from the outside, so it sits level with a button or a field beside
 * it at every density.
 */
export function SwitchBank<T extends string>({
  value,
  onChange,
  options,
  label,
  size = 'default',
  className,
}: {
  value: T
  onChange: (next: T) => void
  options: { value: T; label: React.ReactNode; icon?: React.ReactNode; title?: string }[]
  /** Read out for the group, since the segments alone rarely say what they choose between. */
  label?: string
  /** `sm` for a bank inside a panel's strip, where a full control height would widen the strip. */
  size?: 'default' | 'sm'
  className?: string
}) {
  return (
    <div
      role="group"
      aria-label={label}
      data-slot="switch-bank"
      className={cn(
        'flex w-fit shrink-0 divide-x-(--hairline) overflow-hidden rounded-sm border border-(length:--hairline) bg-card',
        className,
      )}
      style={{ height: size === 'sm' ? 'calc(var(--control-h) - 0.375rem)' : 'var(--control-h)' }}
    >
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          title={o.title}
          aria-pressed={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            'flex items-center gap-1.5 whitespace-nowrap font-medium transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
            size === 'sm' ? 'px-2' : 'px-2.5',
            value === o.value
              ? 'bg-accent text-accent-foreground'
              : 'text-muted-foreground hover:bg-muted hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)' }}
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  )
}
