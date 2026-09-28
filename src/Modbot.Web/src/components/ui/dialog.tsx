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
 * @param place `sheet`, the default: centered on a desk, and a sheet along the bottom of a phone
 *   (the `sheet` variant in `index.css`), under the thumb that opened it rather than in the middle
 *   of the screen (mobile review 2026-09-28, #8). `full` and `side`: the caller places the dialog
 *   with its own classes, which the sheet's would otherwise outrank on a phone.
 */
function DialogContent({
  className,
  bodyClassName,
  children,
  title,
  subtitle,
  lead,
  actions,
  foot,
  place = 'sheet',
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Content> & {
  title: string
  subtitle?: React.ReactNode
  lead?: React.ReactNode
  actions?: React.ReactNode
  /** Drawn under the body and never scrolled away with it. A row of buttons is a `DialogFoot`. */
  foot?: React.ReactNode
  bodyClassName?: string
  place?: 'sheet' | 'full' | 'side'
}) {
  const sheet = place === 'sheet'
  // A confirmation can be a title and its buttons with nothing between them, and an empty body
  // drew a blank band there.
  const hasBody = React.Children.toArray(children).length > 0

  return (
    <DialogPrimitive.Portal>
      {/* The overlay and the dialog share one layer, so the page order decides: a dialog opened
          from another one comes later, and its overlay dims the one behind it. */}
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 dark:bg-background/70" />
      <DialogPrimitive.Content
        className={cn(
          'fixed z-40 flex flex-col rounded-sm border border-(length:--hairline) bg-card p-0 text-card-foreground shadow-sm outline-none',
          sheet && [
            // Never wider than the screen and never taller than it either. A dialog that ran off
            // the bottom of a phone had no scrollbar of its own and nothing could reach its Save
            // button; the body below scrolls instead.
            'top-1/2 left-1/2 max-h-[calc(100dvh-1.5rem)] w-[calc(100vw-1.5rem)] max-w-[520px] -translate-x-1/2 -translate-y-1/2',
            // The whole width, square at the bottom, and never more than most of what is in sight,
            // so the page it came from still shows above it. It stands on the on-screen keyboard
            // rather than behind it (lib/visibleArea.ts), and clear of a notch on a phone on its side.
            'sheet:top-auto sheet:bottom-[var(--keyboard-h,0px)] sheet:left-0 sheet:w-full sheet:max-w-none sheet:translate-x-0 sheet:translate-y-0',
            'sheet:max-h-[calc(var(--visible-h,100dvh)*0.85)] sheet:rounded-b-none sheet:border-x-0 sheet:border-b-0',
            'sheet:pr-[env(safe-area-inset-right)] sheet:pl-[env(safe-area-inset-left)]',
            'sheet:[&_h2]:whitespace-normal sheet:[&_h2]:[overflow-wrap:anywhere]',
          ],
          !hasBody && '[&>[data-slot=dialog-foot]]:border-t-0',
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
        {hasBody && (
          <div
            className={cn(
              'min-h-0 flex-1 overflow-auto px-4 py-4',
              // With nothing pinned under it, the body is the sheet's last row, so it keeps clear
              // of the home bar.
              sheet && !foot && 'sheet:pb-[max(1rem,env(safe-area-inset-bottom))]',
              bodyClassName,
            )}
          >
            {children}
          </div>
        )}
        {foot}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

/**
 * A dialog's row of buttons, for its `foot`: pinned under the body, so a long form or a list of
 * reasons never scrolls Cancel and Ban out of reach. On a phone the buttons share the width, which
 * makes each a wide target for a thumb, and the row keeps clear of the home bar.
 */
function DialogFoot({ className, ...props }: React.ComponentProps<'div'>) {
  return (
    <div
      data-slot="dialog-foot"
      className={cn(
        'flex shrink-0 flex-wrap items-center justify-end gap-2 border-t border-t-(length:--hairline) px-4 py-3',
        'sheet:pb-[max(0.75rem,env(safe-area-inset-bottom))] sheet:*:grow',
        className,
      )}
      {...props}
    />
  )
}

export { Dialog, DialogTrigger, DialogClose, DialogContent, DialogFoot }
