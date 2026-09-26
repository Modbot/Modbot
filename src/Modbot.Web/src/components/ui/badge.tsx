import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "@/lib/utils"
import { Slot } from "radix-ui"

const badgeVariants = cva(
  "inline-flex w-fit shrink-0 items-center justify-center gap-1 overflow-hidden rounded-sm border border-(length:--hairline) px-1.5 py-px text-(length:--text-small) leading-[1.35] font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring aria-invalid:border-destructive [&>svg]:pointer-events-none [&>svg]:size-3",
  {
    variants: {
      variant: {
        default: "border-transparent bg-primary text-primary-foreground [a&]:hover:bg-primary/90",
        secondary:
          "border-border bg-strip text-foreground [a&]:hover:bg-muted",
        destructive:
          "border-destructive/40 bg-destructive/10 text-destructive [a&]:hover:bg-destructive/15",
        ok: "border-ok/40 bg-ok/10 text-ok [a&]:hover:bg-ok/15",
        warn: "border-warn/40 bg-warn/10 text-warn [a&]:hover:bg-warn/15",
        info: "border-info/40 bg-info/10 text-info [a&]:hover:bg-info/15",
        gold: "border-gold/40 bg-gold/10 text-gold [a&]:hover:bg-gold/15",
        outline:
          "border-border text-muted-foreground [a&]:hover:bg-muted [a&]:hover:text-foreground",
        ghost: "border-transparent text-muted-foreground [a&]:hover:bg-muted [a&]:hover:text-foreground",
        link: "border-transparent text-link underline-offset-4 [a&]:hover:underline",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  }
)

function Badge({
  className,
  variant = "default",
  asChild = false,
  ...props
}: React.ComponentProps<"span"> &
  VariantProps<typeof badgeVariants> & { asChild?: boolean }) {
  const Comp = asChild ? Slot.Root : "span"

  return (
    <Comp
      data-slot="badge"
      data-variant={variant}
      className={cn(badgeVariants({ variant }), className)}
      {...props}
    />
  )
}

export { Badge, badgeVariants }
