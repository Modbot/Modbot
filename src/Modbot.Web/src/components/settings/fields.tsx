import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

/** The small furniture every settings card is built from. */

/** A label and a value on one line, for lists of read-only facts. */
export function Row({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="flex justify-between gap-4 py-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right tabular-nums" title={title}>{value}</span>
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
      <div className="truncate font-medium tabular-nums" title={value}>
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
          'border-input placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50',
          'dark:bg-input/30 flex w-full rounded-md border bg-transparent px-3 py-2 text-base shadow-xs',
          'transition-[color,box-shadow] outline-none focus-visible:ring-[3px] md:text-sm',
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
          'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
          checked ? 'bg-primary' : 'bg-input',
        )}
      >
        <span
          className={cn(
            'inline-block size-4 rounded-full bg-background shadow-sm transition-transform',
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
 * A tinted box for something the operator has to read before the controls make sense: a lock, a
 * warning, a reason a setting cannot be changed. Tone is a hint, never the whole message — the
 * title says what is wrong in words.
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
  const tint = {
    ok: 'border-ok/40 bg-ok/10',
    warn: 'border-warn/40 bg-warn/10',
    danger: 'border-destructive/40 bg-destructive/10',
    neutral: 'border-border bg-secondary',
  }[tone]

  return (
    <div
      className={cn('rounded-lg border px-4 py-3', tint, className)}
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="font-medium">{title}</div>
          {children && (
            <div
              className="mt-1 flex flex-col gap-1 text-muted-foreground"
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
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
