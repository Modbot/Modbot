import { SET_UP_LINK, type CalendarSwitchable } from '@/lib/calendarPlaces'
import { followLink } from '@/lib/router'
import { cn } from '@/lib/utils'

/**
 * "Not set up", linking to where the place is set up (calendar design §14.3). In the form it opens
 * in a new tab, so what was typed stays; elsewhere it moves in place.
 */
export function NotSetUp({
  place,
  newTab = false,
  className,
}: {
  place: CalendarSwitchable
  newTab?: boolean
  className?: string
}) {
  const to = SET_UP_LINK[place]

  return (
    <a
      href={to}
      target={newTab ? '_blank' : undefined}
      rel={newTab ? 'noreferrer' : undefined}
      onClick={newTab ? undefined : followLink(to)}
      className={cn('text-destructive underline underline-offset-2', className)}
      style={{ fontSize: 'var(--text-small)' }}
    >
      Not set up
    </a>
  )
}
