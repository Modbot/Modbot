import { Card } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'

/** The small furniture every settings card is built from. */

/**
 * A label and a value on one line, for lists of read-only facts. `mono` for a machine value (an
 * id, a version, an address, a count, a size, a time); a word or a name stays in the body face.
 */
export function Row({
  label,
  value,
  title,
  mono = false,
}: {
  label: string
  value: React.ReactNode
  title?: string
  mono?: boolean
}) {
  return (
    <div className="flex justify-between gap-4 py-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="shrink-0 text-muted-foreground">{label}</span>
      <span className={cn('min-w-0 text-right break-words tabular-nums', mono && 'font-mono')} title={title}>
        {value}
      </span>
    </div>
  )
}

/** A label above a value, for a few facts laid out side by side. `mono` as on `Row`. */
export function Fact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </div>
      <div className={cn('truncate font-medium tabular-nums', mono && 'font-mono')} title={value}>
        {value}
      </div>
    </div>
  )
}

export function Field({
  label,
  value,
  placeholder,
  onChange,
}: {
  label: string
  value: string
  placeholder: string
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

/** A field for a whole number. The value stays a string so an empty box stays empty. */
export function NumberField({
  label,
  value,
  min,
  max,
  onChange,
}: {
  label: string
  value: string
  min?: number
  max?: number
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        type="number"
        inputMode="numeric"
        min={min}
        max={max}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

/** A field for something longer than one line -- a message, a note. */
export function LongField({
  label,
  value,
  placeholder,
  rows = 3,
  onChange,
}: {
  label: string
  value: string
  placeholder: string
  rows?: number
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Textarea
        rows={rows}
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

export function PasswordField({
  label,
  value,
  onChange,
}: {
  label: string
  value: string
  onChange: (v: string) => void
}) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <Input
        type="password"
        autoComplete="new-password"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}

/**
 * A tick box with its label beside it, for one choice among several that are each on or off. The
 * box is half the control height, so it grows with the density, and the tick is drawn on the
 * input itself: two edges of a turned box in the primary's foreground.
 */
export function Checkbox({
  checked,
  disabled,
  onChange,
  children,
}: {
  checked: boolean
  disabled?: boolean
  onChange: (checked: boolean) => void
  children: React.ReactNode
}) {
  return (
    <label
      className={cn('flex items-center gap-2', disabled ? 'opacity-50' : 'cursor-pointer')}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className={cn(
          'relative size-[calc(var(--control-h)/2)] shrink-0 cursor-[inherit] appearance-none rounded-sm border border-(length:--hairline) border-input bg-card transition-colors',
          'outline-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
          'checked:border-primary checked:bg-primary',
          'before:absolute before:top-[42%] before:left-1/2 before:h-[55%] before:w-[30%] before:-translate-x-1/2 before:-translate-y-1/2 before:rotate-45 before:border-r-2 before:border-b-2 before:border-primary-foreground before:opacity-0 checked:before:opacity-100',
        )}
      />
      {children}
    </label>
  )
}

/**
 * An on/off switch with its label beside it. The track is two thirds of the control height and
 * the row is a control high, so the switch grows with the density the way a button does, and at
 * VR the row is the 48px target.
 */
export function Switch({
  checked,
  disabled,
  onChange,
  children,
}: {
  checked: boolean
  disabled?: boolean
  onChange: (checked: boolean) => void
  children: React.ReactNode
}) {
  return (
    <label
      className={cn('flex min-h-(--control-h) w-fit items-center gap-3', disabled ? 'opacity-50' : 'cursor-pointer')}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={cn(
          'relative inline-flex h-(--switch-h) w-[calc(var(--switch-h)*1.8)] shrink-0 items-center rounded-sm transition-colors',
          'outline-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
          checked ? 'bg-primary' : 'bg-input',
        )}
        style={{ '--switch-h': 'calc(var(--control-h) * 2 / 3)' } as React.CSSProperties}
      >
        <span
          className={cn(
            'inline-block size-[calc(var(--switch-h)-4px)] rounded-xs bg-background transition-transform',
            checked ? 'translate-x-[calc(var(--switch-h)*0.8+2px)]' : 'translate-x-[2px]',
          )}
        />
      </button>
      <span className="font-medium">{children}</span>
    </label>
  )
}

/** Muted text at the small size. */
export function Hint({ className, children }: { className?: string; children: React.ReactNode }) {
  return (
    <p className={cn('text-muted-foreground', className)} style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </p>
  )
}

/** The outcome of a save or a check, beside the button that caused it. */
export function Outcome({
  tone,
  children,
}: {
  tone: 'ok' | 'problem'
  children: React.ReactNode
}) {
  if (!children) return null

  return (
    <span
      className={tone === 'ok' ? 'text-ok' : 'text-destructive'}
      style={{ fontSize: 'var(--text-small)' }}
    >
      {children}
    </span>
  )
}

/** "Loading…" and load failures, filling the row a section's cards would have taken. */
export function Placeholder({ children }: { children: React.ReactNode }) {
  return (
    <Card className="col-span-12">
      <EmptyRow>{children}</EmptyRow>
    </Card>
  )
}
