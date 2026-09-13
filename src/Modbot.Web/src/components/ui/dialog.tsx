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

function DialogContent({
  className,
  children,
  title,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Content> & { title: string }) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 backdrop-blur-[2px]" />
      <DialogPrimitive.Content
        className={cn(
          'fixed top-1/2 left-1/2 z-50 w-full max-w-[520px] -translate-x-1/2 -translate-y-1/2 rounded-xl border bg-card p-0 text-card-foreground shadow-lg outline-none',
          className,
        )}
        {...props}
      >
        <div className="flex items-center justify-between border-b px-5 py-3" style={{ borderBottomWidth: 'var(--hairline)' }}>
          <DialogPrimitive.Title className="font-semibold tracking-tight">{title}</DialogPrimitive.Title>
          <DialogPrimitive.Close
            className="rounded-md p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
            aria-label="Close"
          >
            <X className="size-4" />
          </DialogPrimitive.Close>
        </div>
        <div className="px-5 py-4">{children}</div>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}

export { Dialog, DialogTrigger, DialogClose, DialogContent }
