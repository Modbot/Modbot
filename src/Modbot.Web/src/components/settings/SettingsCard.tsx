import {
  Card,
  CardAction,
  CardContent,
  CardFooter,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import { cn } from '@/lib/utils'

// One settings section is a titled group of cards on a 12-column grid, and one card is one
// thing an operator can read or change. Another feature adds its settings as one more
// SettingsCard inside an existing section, or one more SettingsSection listed in Settings.tsx.

/**
 * One settings tab's content: a grid of cards. Cards span 6 columns by default and stack to one
 * column below `lg`. The heading is for screen readers only, because the tab above already shows
 * the same name.
 */
export function SettingsSection({
  id,
  title,
  children,
}: {
  id: string
  title: string
  children: React.ReactNode
}) {
  return (
    <section id={id} aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`} className="sr-only">
        {title}
      </h2>
      <div className="grid grid-cols-12 gap-4">{children}</div>
    </section>
  )
}

/**
 * One card on the settings grid.
 *
 * `span` is the width on wide screens: 6 for most cards, 12 for the few that genuinely need the
 * room (a chart, a form with many columns). Everything narrower than `lg` gets one card per row.
 * `footer` is where the card's buttons and their "Saved." / error text go, so every card puts its
 * actions in the same place.
 */
export function SettingsCard({
  title,
  action,
  footer,
  span = 6,
  className,
  children,
}: {
  title: string
  /** Something small in the top-right corner, such as a re-check button. */
  action?: React.ReactNode
  footer?: React.ReactNode
  span?: 6 | 12
  className?: string
  children: React.ReactNode
}) {
  return (
    <Card
      className={cn(
        'col-span-12 gap-3 py-5',
        span === 12 ? 'lg:col-span-12' : 'lg:col-span-6',
        className,
      )}
    >
      <CardHeader className="gap-1 px-5">
        <CardTitle className="font-medium">{title}</CardTitle>
        {action && <CardAction>{action}</CardAction>}
      </CardHeader>
      <CardContent className="flex flex-1 flex-col gap-3 px-5">{children}</CardContent>
      {footer && <CardFooter className="flex-wrap gap-3 px-5">{footer}</CardFooter>}
    </Card>
  )
}
