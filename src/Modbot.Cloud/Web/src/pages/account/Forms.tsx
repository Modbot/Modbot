import type { ReactNode } from 'react'
import { Input } from '@/components/ui/input'

/** A labelled box. The label names the control; nothing explains it. */
export function Field({
  id,
  label,
  type = 'text',
  value,
  autoComplete,
  invalid,
  onChange,
}: {
  id: string
  label: string
  type?: string
  value: string
  autoComplete?: string
  invalid?: boolean
  onChange: (value: string) => void
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="font-medium">
        {label}
      </label>
      <Input
        id={id}
        type={type}
        autoComplete={autoComplete}
        value={value}
        aria-invalid={invalid ? true : undefined}
        onChange={(e) => onChange(e.target.value)}
      />
    </div>
  )
}

export function Problem({ children }: { children: ReactNode }) {
  if (!children) return null

  return (
    <p role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </p>
  )
}

export function Done({ children }: { children: ReactNode }) {
  if (!children) return null

  return (
    <p role="status" style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </p>
  )
}
