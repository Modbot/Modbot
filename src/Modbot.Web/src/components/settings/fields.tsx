import { Card } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

/** The small furniture every settings card is built from. */

/** Mono for anything with a figure in it (a count, a size, a version, a time, a commit), the body face for a word like "Off". */
function face(value: string) {
  return /\d/.test(value) ? 'font-mono' : undefined
}

/** A label and a value on one line, for lists of read-only facts. */
export function Row({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="flex justify-between gap-4 py-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="shrink-0 text-muted-foreground">{label}</span>
      <span className={cn('min-w-0 text-right break-words tabular-nums', face(value))} title={title}>{value}</span>
    </div>
  )
}

/** A label above a value, for a few facts laid out side by side. */
export function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </div>
      <div className={cn('truncate font-medium tabular-nums', face(value))} title={value}>
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
      <textarea
        className={cn(
          'flex w-full rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 py-1 text-base',
          'transition-colors outline-none placeholder:text-muted-foreground md:text-(length:--text-base)',
          'focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring',
        )}
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
    <label className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      {children}
    </label>
  )
}

/** An on/off switch with its label beside it. */
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
      className={cn('flex w-fit items-center gap-3', disabled ? 'opacity-50' : 'cursor-pointer')}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={cn(
          'relative inline-flex h-5 w-9 shrink-0 items-center rounded-sm transition-colors',
          'outline-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
          checked ? 'bg-primary' : 'bg-input',
        )}
      >
        <span
          className={cn(
            'inline-block size-4 rounded-xs bg-background transition-transform',
            checked ? 'translate-x-[18px]' : 'translate-x-[2px]',
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

/**
 * A tinted band for something the operator has to read before the controls make sense: a lock, a
 * warning, a reason a setting cannot be changed. Tone is a hint, never the whole message — the
 * title says what is wrong in words, after a filled square in the tone's colour.
 */
export function Notice({
  tone,
  title,
  action,
  className,
  children,
}: {
  tone: 'ok' | 'warn' | 'danger' | 'neutral'
  title: string
  action?: React.ReactNode
  className?: string
  children?: React.ReactNode
}) {
  // Set inline so the tint survives a PanelGrid, whose cells are otherwise painted the card colour.
  const tint = {
    ok: 'color-mix(in oklab, var(--ok) 10%, var(--card))',
    warn: 'color-mix(in oklab, var(--warn) 10%, var(--card))',
    danger: 'color-mix(in oklab, var(--destructive) 10%, var(--card))',
    neutral: 'var(--strip)',
  }[tone]

  const square = {
    ok: 'bg-ok',
    warn: 'bg-warn',
    danger: 'bg-destructive',
    neutral: 'bg-muted-foreground',
  }[tone]

  return (
    <div
      className={cn('px-(--panel-pad) py-2', className)}
      style={{ background: tint }}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex items-start gap-2 font-medium">
            <span aria-hidden className={cn('mt-[0.45em] size-2 shrink-0', square)} />
            {title}
          </div>
          {children && (
            <div
              className="mt-1 flex flex-col gap-1 pl-4 text-muted-foreground"
              style={{ fontSize: 'var(--text-small)' }}
            >
              {children}
            </div>
          )}
        </div>
        {action}
      </div>
    </div>
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
