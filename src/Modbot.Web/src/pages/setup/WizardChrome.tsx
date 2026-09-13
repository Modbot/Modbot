import type * as React from 'react'
import { cn } from '@/lib/utils'

/**
 * The wizard's shared furniture, ported from the approved design prototype
 * (`explore/design/index.html`): the centred card, the step indicator, the labelled field and the
 * coloured note.
 *
 * The wizard sits outside the app shell, so none of the sidebar or topbar chrome applies. It
 * still reads from the same tokens in index.css, which is what keeps it from drifting into
 * looking like a different product.
 */

export function Brand({ subtitle }: { subtitle?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 pb-5">
      <div className="grid size-7 shrink-0 place-items-center rounded-md bg-primary text-sm font-semibold text-primary-foreground">
        M
      </div>
      <div className="leading-tight">
        <div className="font-semibold tracking-tight">Modbot</div>
        {subtitle && (
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {subtitle}
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * Progress as filled bars rather than numbered circles.
 *
 * Five steps is too many for numbered circles to stay legible at this width, and the bar reads as
 * "how far through" at a glance without anyone having to count.
 */
export function StepIndicator({ total, current }: { total: number; current: number }) {
  return (
    <div className="flex gap-1.5 px-6 pt-4" role="progressbar" aria-valuenow={current} aria-valuemin={1} aria-valuemax={total}>
      {Array.from({ length: total }, (_, i) => (
        <span
          key={i}
          className={cn('h-[3px] flex-1 rounded-sm', i < current ? 'bg-primary' : 'bg-secondary')}
        />
      ))}
    </div>
  )
}

export function WizardHeader({
  eyebrow,
  title,
  children,
}: {
  eyebrow: string
  title: string
  children?: React.ReactNode
}) {
  return (
    <div className="px-6 pt-6 pb-4">
      <div className="text-[0.6875rem] font-semibold tracking-[0.05em] text-primary uppercase">
        {eyebrow}
      </div>
      <h2 className="mt-2 mb-1 text-[19px] font-semibold tracking-tight">{title}</h2>
      {children && (
        <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {children}
        </p>
      )}
    </div>
  )
}

export function WizardBody({ children }: { children: React.ReactNode }) {
  return <div className="space-y-4 px-6 pb-5">{children}</div>
}

export function WizardFooter({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="flex items-center gap-2 border-t bg-secondary px-6 py-4"
      style={{ borderTopWidth: 'var(--hairline)' }}
    >
      {children}
    </div>
  )
}

export function Field({
  label,
  hint,
  children,
  htmlFor,
}: {
  label: string
  hint?: string
  htmlFor: string
  children: React.ReactNode
}) {
  return (
    <div>
      <label
        htmlFor={htmlFor}
        className="mb-1 block text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {label}
        {hint && <span className="text-muted-foreground/70"> — {hint}</span>}
      </label>
      {children}
    </div>
  )
}

/**
 * A coloured left edge, not a coloured box.
 *
 * Same reasoning as the destructive button in the prototype: colour is the last signal, after
 * placement and shape. A note that is legible only because it is green fails for a colour-blind
 * moderator and stops registering for everyone else after the tenth time they see it.
 */
export function Note({
  tone = 'info',
  title,
  children,
}: {
  tone?: 'info' | 'ok' | 'warn' | 'danger'
  title?: string
  children?: React.ReactNode
}) {
  const edge = {
    info: 'var(--info)',
    ok: 'var(--ok)',
    warn: 'var(--warn)',
    danger: 'var(--destructive)',
  }[tone]

  return (
    <div
      className="rounded-md border bg-secondary p-3 text-muted-foreground"
      style={{ borderLeft: `2px solid ${edge}`, fontSize: 'var(--text-small)' }}
    >
      {title && <span className="font-semibold text-foreground">{title} </span>}
      {children}
    </div>
  )
}

/** Inline validation and request failures, in one consistent place. */
export function ErrorText({ children }: { children?: React.ReactNode }) {
  if (!children) return null

  return (
    <p className="m-0 text-destructive" style={{ fontSize: 'var(--text-small)' }} role="alert">
      {children}
    </p>
  )
}
