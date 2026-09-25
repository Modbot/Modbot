import * as React from "react"
import { cn } from "@/lib/utils"

/**
 * A tinted band for something to read before the controls around it make sense: a lock, a
 * warning, a result, a reason a setting cannot be changed.
 *
 * Tone is a hint, never the whole message. The words say what is wrong, after a filled square in
 * the tone's colour, so a note that would only be legible because it is green still reads for a
 * colour-blind moderator, and still registers the tenth time somebody sees it.
 *
 * It draws no edge of its own: inside a panel's padding the tint marks it out, and as a PanelGrid
 * cell or straight under a strip it runs to the panel's edges.
 */
function Notice({
  tone = "neutral",
  title,
  action,
  className,
  children,
}: {
  tone?: "ok" | "warn" | "danger" | "neutral"
  /** The line itself. Without one, the children are the line. */
  title?: React.ReactNode
  /** One control on the right, such as a re-check. */
  action?: React.ReactNode
  className?: string
  children?: React.ReactNode
}) {
  // Set inline so the tint survives a PanelGrid, whose cells are otherwise painted the card colour.
  const tint = {
    ok: "color-mix(in oklab, var(--ok) 10%, var(--card))",
    warn: "color-mix(in oklab, var(--warn) 10%, var(--card))",
    danger: "color-mix(in oklab, var(--destructive) 10%, var(--card))",
    neutral: "var(--strip)",
  }[tone]

  const square = {
    ok: "bg-ok",
    warn: "bg-warn",
    danger: "bg-destructive",
    neutral: "bg-muted-foreground",
  }[tone]

  return (
    <div
      data-slot="notice"
      className={cn("flex items-start justify-between gap-4 px-(--panel-pad) py-2", className)}
      style={{ background: tint, fontSize: title ? undefined : "var(--text-small)" }}
    >
      <div className="flex min-w-0 items-start gap-2">
        <span aria-hidden className={cn("mt-[0.45em] size-2 shrink-0", square)} />
        <div className="min-w-0">
          {title && <div className="font-medium">{title}</div>}
          {children && (
            <div
              className={cn("text-muted-foreground", title && "mt-1")}
              style={{ fontSize: "var(--text-small)" }}
            >
              {children}
            </div>
          )}
        </div>
      </div>
      {action}
    </div>
  )
}

export { Notice }
