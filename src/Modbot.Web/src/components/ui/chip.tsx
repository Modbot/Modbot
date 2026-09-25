import * as React from "react"
import { cn } from "@/lib/utils"

/**
 * A button that stays pressed while its choice is on: a filter, a role on a list.
 *
 * For choices that are each on or off by themselves. A choice where exactly one value is picked is
 * a SwitchBank, which wraps when its values do not fit on one line.
 */
function Chip({
  on,
  onClick,
  children,
}: {
  on: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      data-slot="chip"
      aria-pressed={on}
      onClick={onClick}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-sm border border-(length:--hairline) border-input px-2.5 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring",
        on ? "bg-accent text-accent-foreground" : "bg-card text-muted-foreground hover:bg-muted hover:text-foreground"
      )}
      style={{ fontSize: "var(--text-small)", height: "var(--control-h)" }}
    >
      {children}
    </button>
  )
}

export { Chip }
