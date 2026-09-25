import * as React from "react"
import { cn } from "@/lib/utils"

/*
 * A card is a panel in the console: a flat box with one hairline edge and square corners, and no
 * inset of its own. Spacing belongs to what is inside it (the header strip, the content, the
 * footer strip), so a table can run to the panel's edges and a panel in a PanelGrid can share its
 * edge with the next one.
 */
function Card({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card"
      className={cn(
        "flex flex-col border border-(length:--hairline) bg-card text-card-foreground",
        className
      )}
      {...props}
    />
  )
}

/** The thin strip across the top of a panel: its label on the left, one action on the right. */
function CardHeader({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-header"
      className={cn(
        "flex min-h-(--strip-h) flex-wrap items-center gap-x-3 gap-y-1 border-b border-b-(length:--hairline) bg-strip px-(--panel-pad) py-1",
        className
      )}
      {...props}
    />
  )
}

function CardTitle({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-title"
      className={cn("font-label leading-tight", className)}
      {...props}
    />
  )
}

function CardDescription({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-description"
      className={cn("text-(length:--text-small) text-muted-foreground", className)}
      {...props}
    />
  )
}

function CardAction({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-action"
      className={cn("ml-auto flex items-center gap-2", className)}
      {...props}
    />
  )
}

function CardContent({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-content"
      className={cn("px-(--panel-pad) py-(--panel-pad)", className)}
      {...props}
    />
  )
}

/** The strip along the foot of a panel, where its buttons and their result text go. */
function CardFooter({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="card-footer"
      className={cn(
        "mt-auto flex min-h-(--strip-h) items-center border-t border-t-(length:--hairline) bg-strip px-(--panel-pad) py-1",
        className
      )}
      {...props}
    />
  )
}

export {
  Card,
  CardHeader,
  CardFooter,
  CardTitle,
  CardAction,
  CardDescription,
  CardContent,
}
