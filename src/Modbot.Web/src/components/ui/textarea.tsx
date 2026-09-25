import * as React from "react"
import { cn } from "@/lib/utils"

/** A box for more than one line of text, drawn like Input: the same edge, surface and focus. */
function Textarea({ className, ...props }: React.ComponentProps<"textarea">) {
  return (
    <textarea
      data-slot="textarea"
      className={cn(
        "block w-full min-w-0 rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 py-1 text-base transition-colors outline-none selection:bg-primary selection:text-primary-foreground placeholder:text-muted-foreground disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-(length:--text-base)",
        "focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring",
        "aria-invalid:border-destructive",
        className
      )}
      {...props}
    />
  )
}

export { Textarea }
