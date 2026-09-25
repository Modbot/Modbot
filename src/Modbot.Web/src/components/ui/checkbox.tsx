import { useEffect, useRef } from 'react'
import { cn } from '@/lib/utils'

/**
 * A tick box with its label beside it, for one choice among several that are each on or off. The
 * box is half the control height, so it grows with the density, and the tick is drawn on the
 * input itself: two edges of a turned box in the primary's foreground. `mixed` is the box over a
 * group that is partly ticked, and draws a bar instead of the tick.
 *
 * The box sits on the label's first line, so a label that wraps keeps its box beside the start of
 * it rather than halfway down. Where the label is given a floor taller than its text (a phone),
 * the box and the text are centred in it together.
 */
export function Checkbox({
  id,
  checked,
  mixed = false,
  disabled,
  title,
  className,
  onChange,
  children,
}: {
  id?: string
  checked: boolean
  mixed?: boolean
  disabled?: boolean
  /** Said on hover over the whole row, such as why the box cannot be ticked. */
  title?: string
  className?: string
  onChange: (checked: boolean) => void
  children: React.ReactNode
}) {
  const ref = useRef<HTMLInputElement>(null)

  // `indeterminate` is a property with no attribute, so it can only be set on the element.
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = mixed
  }, [mixed])

  return (
    <label
      htmlFor={id}
      title={title}
      className={cn(
        'grid grid-cols-[auto_1fr] content-center items-start gap-x-2',
        disabled ? 'opacity-50' : 'cursor-pointer',
        className,
      )}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <input
        ref={ref}
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className={cn(
          'relative size-[calc(var(--control-h)/2)] shrink-0 cursor-[inherit] appearance-none rounded-sm border border-(length:--hairline) border-input bg-card transition-colors',
          // Set in the label's size and line height, so `1lh` is a line of the label's text.
          '[font-size:inherit] [line-height:inherit] mt-[calc((1lh_-_var(--control-h)/2)/2)]',
          'outline-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
          'checked:border-primary checked:bg-primary indeterminate:border-primary indeterminate:bg-primary',
          'before:absolute before:top-[42%] before:left-1/2 before:h-[55%] before:w-[30%] before:-translate-x-1/2 before:-translate-y-1/2 before:rotate-45 before:border-r-2 before:border-b-2 before:border-primary-foreground before:opacity-0 checked:before:opacity-100 indeterminate:before:opacity-0',
          'after:absolute after:top-1/2 after:left-1/2 after:h-0.5 after:w-1/2 after:-translate-x-1/2 after:-translate-y-1/2 after:bg-primary-foreground after:opacity-0 indeterminate:after:opacity-100',
        )}
      />
      <span className="min-w-0">{children}</span>
    </label>
  )
}
