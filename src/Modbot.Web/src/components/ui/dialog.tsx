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
      {/* The overlay and the dialog share one layer, so the page order decides: a dialog opened
          from another one comes later, and its overlay dims the one behind it. */}
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 dark:bg-background/70" />
      <DialogPrimitive.Content
        className={cn(
          'fixed top-1/2 left-1/2 z-40 flex -translate-x-1/2 -translate-y-1/2 flex-col rounded-sm border border-(length:--hairline) bg-card p-0 text-card-foreground shadow-sm outline-none',
          // Never wider than the screen and never taller than it either. A dialog that ran off
          // the bottom of a phone had no scrollbar of its own and nothing could reach its Save
          // button; the body below scrolls instead.
          'max-h-[calc(100dvh-1.5rem)] w-[calc(100vw-1.5rem)] max-w-[520px]',
          className,
        )}
        {...props}
      >
        <div
          className="flex shrink-0 items-center gap-3 border-b-(length:--hairline) bg-strip px-4 py-2"
        >
          {lead}
          <div className="min-w-0 flex-1">
            <DialogPrimitive.Title className="truncate font-label" style={{ fontSize: 'calc(var(--text-base) + 1px)' }}>
              {title}
            </DialogPrimitive.Title>
            {subtitle && (
              <div className="text-muted-foreground [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-small)' }}>
                {subtitle}
              </div>
            )}
          </div>
          {actions}
          {/* Sized from --control-h rather than from the glyph: on a phone this is the way out of
              a dialog that covers the screen, and a 24px target is not one a finger can hit. */}
          <DialogPrimitive.Close
            className="grid shrink-0 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
            style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
            aria-label="Close"
          >
            <X className="size-4" />
          </DialogPrimitive.Close>
        </div>
        <div className={cn('min-h-0 flex-1 overflow-auto px-4 py-4', bodyClassName)}>{children}</div>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

export { Dialog, DialogTrigger, DialogClose, DialogContent }
