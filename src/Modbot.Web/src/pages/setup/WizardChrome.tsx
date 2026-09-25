import type * as React from 'react'
import { CardAction, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'

/**
 * The wizard's shared furniture, ported from the approved design prototype
 * (`explore/design/index.html`): the centred panel, the step indicator and the labelled field.
 *
 * The wizard sits outside the app shell, so none of the sidebar or topbar chrome applies. It
 * still reads from the same tokens in index.css, which is what keeps it from drifting into
 * looking like a different product.
 */

export function Brand({ subtitle }: { subtitle?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 pb-5">
      <img src="/icon-512.png" alt="" width={28} height={28} className="size-7 shrink-0" />
      <div className="leading-tight">
        <div className="font-display text-[0.9375rem] leading-none">Modbot</div>
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
    <div className="flex gap-px" role="progressbar" aria-valuenow={current} aria-valuemin={1} aria-valuemax={total}>
      {Array.from({ length: total }, (_, i) => (
        <span
          key={i}
          className={cn('h-[3px] flex-1', i < current ? 'bg-primary' : 'bg-secondary')}
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
    <>
      <CardHeader>
        <CardTitle>
          <h2>{title}</h2>
        </CardTitle>
        <CardAction className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {eyebrow}
        </CardAction>
      </CardHeader>
      {children && (
        <p className="m-0 px-(--panel-pad) pt-(--panel-pad) text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {children}
        </p>
      )}
    </>
  )
}

export function WizardBody({ children }: { children: React.ReactNode }) {
  return <div className="space-y-4 p-(--panel-pad)">{children}</div>
}

export function WizardFooter({ children }: { children: React.ReactNode }) {
  return <CardFooter className="gap-2">{children}</CardFooter>
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
        {hint && <span className="text-muted-foreground/70"> · {hint}</span>}
      </label>
      {children}
    </div>
  )
}

/**
 * A checkbox with its label beside it, for the two screens outside the app shell.
 *
 * The settings pages have their own; this one lives here because the wizard and the invite page
 * share the wizard's card and not the settings chrome.
 */
export function Tickbox({
  id,
  checked,
  onChange,
  children,
}: {
  id: string
  checked: boolean
  onChange: (checked: boolean) => void
  children: React.ReactNode
}) {
  return (
    <label htmlFor={id} className="flex items-start gap-2" style={{ fontSize: 'var(--text-small)' }}>
      <input
        id={id}
        type="checkbox"
        className="mt-0.5"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span>{children}</span>
    </label>
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
