import { cn } from '@/lib/utils'

/**
 * A choice of values where exactly one is picked, drawn as one bordered box with a hairline between
 * the segments and the chosen one filled with the accent: density, a date range, Day/Week/Month, a
 * list's views, a provider.
 *
 * Tabs are for switching a page's sections; this is for switching how one thing is shown. The
 * height is the control height from the outside, so it sits level with a button or a field beside
 * it at every density.
 *
 * When the segments do not fit on one line (six filters on a phone) they wrap onto a second row
 * inside the same box. The hairlines are the box's own surface showing through a one-hairline gap,
 * the way the panel grid draws its lines, so a row that wraps is still divided by one line, and
 * each row's segments stretch to its full width so no row ends in an empty stub.
 *
 * `layout="list"` stacks the values as full-width rows instead, for values that are records with a
 * picture and a second line of their own (setup's group list) rather than a word or two. Each row
 * is at least a list row high, and its content keeps its own faces and stays in the foreground
 * colour, because a row that names a group is read, not just picked.
 *
 * `disabled` greys out and locks the whole bank; an option's own `disabled` locks just that one.
 */
export function SwitchBank<T extends string>({
  value,
  onChange,
  options,
  label,
  size = 'default',
  layout = 'row',
  disabled = false,
  className,
}: {
  value: T
  onChange: (next: T) => void
  options: { value: T; label: React.ReactNode; icon?: React.ReactNode; title?: string; disabled?: boolean }[]
  /** Read out for the group, since the segments alone rarely say what they choose between. */
  label?: string
  /** `sm` for a bank inside a panel's strip, where a full control height would widen the strip. */
  size?: 'default' | 'sm'
  /** `list` for one full-width row per value, stacked. */
  layout?: 'row' | 'list'
  disabled?: boolean
  className?: string
}) {
  const list = layout === 'list'

  return (
    <div
      role="group"
      aria-label={label}
      aria-disabled={disabled || undefined}
      data-slot="switch-bank"
      className={cn(
        'flex max-w-full gap-(--hairline) overflow-hidden rounded-sm border border-(length:--hairline) bg-border',
        list ? 'w-full flex-col' : 'w-fit flex-wrap',
        className,
      )}
    >
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          title={o.title}
          aria-pressed={value === o.value}
          disabled={disabled || o.disabled}
          onClick={() => onChange(o.value)}
          className={cn(
            'flex grow items-center gap-1.5 transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring disabled:pointer-events-none disabled:opacity-50',
            list
              ? 'min-h-(--row-h) w-full justify-start px-(--panel-pad) py-2 text-left'
              : cn('justify-center whitespace-nowrap font-medium', size === 'sm' ? 'px-2' : 'px-2.5'),
            value === o.value
              ? 'bg-accent text-accent-foreground'
              : cn('bg-card hover:bg-muted', list ? 'text-foreground' : 'text-muted-foreground hover:text-foreground'),
          )}
          style={
            list
              ? undefined
              : {
                  fontSize: 'var(--text-small)',
                  height:
                    size === 'sm'
                      ? 'calc(var(--control-h) - 0.375rem - 2 * var(--hairline))'
                      : 'calc(var(--control-h) - 2 * var(--hairline))',
                }
          }
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  )
}
