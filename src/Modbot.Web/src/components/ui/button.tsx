import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "@/lib/utils"
import { Slot } from "radix-ui"

const buttonVariants = cva(
  "inline-flex shrink-0 items-center justify-center gap-2 rounded-sm text-(length:--text-base) font-medium whitespace-nowrap transition-colors outline-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90",
        destructive:
          "bg-destructive text-destructive-foreground hover:bg-destructive/90",
        outline:
          "border border-(length:--hairline) border-input bg-card text-foreground hover:bg-muted",
        secondary:
          "border border-(length:--hairline) border-border bg-strip text-foreground hover:bg-muted",
        ghost:
          "text-muted-foreground hover:bg-muted hover:text-foreground",
        link: "text-link underline-offset-4 hover:underline",
      },
      size: {
        default: "h-(--control-h) px-3 has-[>svg]:px-2.5",
        xs: "h-[calc(var(--control-h)-0.375rem)] gap-1 px-2 text-(length:--text-small) has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
        sm: "h-[calc(var(--control-h)-0.125rem)] gap-1.5 px-2.5 has-[>svg]:px-2",
        lg: "h-[calc(var(--control-h)+0.25rem)] px-5 has-[>svg]:px-4",
        icon: "size-(--control-h)",
        "icon-xs": "size-[calc(var(--control-h)-0.375rem)] [&_svg:not([class*='size-'])]:size-3",
        "icon-sm": "size-[calc(var(--control-h)-0.125rem)]",
        "icon-lg": "size-[calc(var(--control-h)+0.25rem)]",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
)

function Button({
  className,
  variant = "default",
  size = "default",
  asChild = false,
  ...props
}: React.ComponentProps<"button"> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean
  }) {
  const Comp = asChild ? Slot.Root : "button"

  return (
    <Comp
      data-slot="button"
      data-variant={variant}
      data-size={size}
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  )
}

export { Button, buttonVariants }
