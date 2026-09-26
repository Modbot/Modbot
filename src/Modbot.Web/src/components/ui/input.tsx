import * as React from "react"
import { cn } from "@/lib/utils"

/**
 * A day field draws itself: the date in mono like every other time, as wide as the whole date at
 * every density (`ch` follows the mono face), and an empty field reads its `mm/dd/yyyy` in the
 * placeholder's muted colour. The browser's calendar button stays, because in Chromium it is the
 * only way to open the calendar with the mouse, and is drawn at the size and weight of a
 * dropdown's chevron.
 */
function dayField(value: React.ComponentProps<"input">["value"]) {
  return cn(
    "w-[calc(10ch+3.5rem)] font-mono",
    "[&::-webkit-calendar-picker-indicator]:ms-1.5 [&::-webkit-calendar-picker-indicator]:size-3.5 [&::-webkit-calendar-picker-indicator]:cursor-pointer [&::-webkit-calendar-picker-indicator]:opacity-60",
    !value && "text-muted-foreground focus:text-foreground"
  )
}

function Input({ className, type, ...props }: React.ComponentProps<"input">) {
  return (
    <input
      type={type}
      data-slot="input"
      className={cn(
        "h-(--control-h) w-full min-w-0 rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 py-1 text-base transition-colors outline-none selection:bg-primary selection:text-primary-foreground file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium file:text-foreground placeholder:text-muted-foreground disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-(length:--text-base)",
        "focus-visible:border-ring focus-visible:ring-1 focus-visible:ring-ring",
        "aria-invalid:border-destructive",
        type === "date" && dayField(props.value ?? props.defaultValue),
        className
      )}
      {...props}
    />
  )
}

export { Input }
