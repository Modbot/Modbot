import * as React from 'react'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { X } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * A modal, on Radix's primitive: focus trapped, Escape closes, the page behind is inert.
 *
 * Kept to the pieces the app uses. The full shadcn set has footers, descriptions and triggers;
 * those can be added when a screen needs one.
 */
const Dialog = DialogPrimitive.Root
const DialogTrigger = DialogPrimitive.Trigger
const DialogClose = DialogPrimitive.Close

/**
 * @param title The heading, and what a screen reader announces. Required, so no dialog is nameless.
 * @param lead Drawn before the heading — a back control, when one dialog was opened from another.
 * @param actions Drawn after the heading, before the close button.
 * @param bodyClassName Overrides the body padding, for a dialog that lays out its own columns.
 */
function DialogContent({
  className,
  bodyClassName,
  children,
  title,
  subtitle,
  lead,
  actions,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Content> & {
  title: string
  subtitle?: React.ReactNode
  lead?: React.ReactNode
  actions?: React.ReactNode
  bodyClassName?: string
}) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 backdrop-blur-[2px]" />
      <DialogPrimitive.Content
        className={cn(
          'fixed top-1/2 left-1/2 z-50 flex w-full max-w-[520px] -translate-x-1/2 -translate-y-1/2 flex-col rounded-xl border bg-card p-0 text-card-foreground shadow-lg outline-none',
          className,
        )}
        {...props}
      >
        <div
          className="flex shrink-0 items-center gap-3 border-b px-5 py-3"
          style={{ borderBottomWidth: 'var(--hairline)' }}
        >
          {lead}
          <div className="min-w-0 flex-1">
            <DialogPrimitive.Title className="truncate font-semibold tracking-tight">
              {title}
            </DialogPrimitive.Title>
            {subtitle && (
              <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {subtitle}
              </div>
            )}
          </div>
          {actions}
          <DialogPrimitive.Close
            className="shrink-0 rounded-md p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
            aria-label="Close"
          >
            <X className="size-4" />
          </DialogPrimitive.Close>
        </div>
        <div className={cn('min-h-0 flex-1 px-5 py-4', bodyClassName)}>{children}</div>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

export { Dialog, DialogTrigger, DialogClose, DialogContent }
