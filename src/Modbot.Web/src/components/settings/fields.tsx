import { Card } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'

/** The small furniture every settings card is built from. */

export { Checkbox } from '@/components/ui/checkbox'
export { Fact, Row } from '@/components/ui/fact-row'

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

/**
 * "Loading…" and load failures, filling the row a section's cards would have taken. A failure
 * passes `tone="danger"`.
 */
export function Placeholder({
  tone,
  children,
}: {
  tone?: 'neutral' | 'danger'
  children: React.ReactNode
}) {
  return (
    <Card className="col-span-12">
      <EmptyRow tone={tone}>{children}</EmptyRow>
    </Card>
  )
}
