import { cn } from '@/lib/utils'

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

/**
 * A label above a value, for a few facts laid out side by side. `mono` as on `Row`. The value wraps,
 * so a sentence such as "Not measurable yet" reads whole in a narrow column at VR.
 */
export function Fact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </div>
      <div className={cn('font-medium break-words tabular-nums', mono && 'font-mono')} title={value}>
        {value}
      </div>
    </div>
  )
}
