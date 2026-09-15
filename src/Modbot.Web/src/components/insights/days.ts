import { longDay } from '@/components/charts'
import type { Insight } from '@/lib/api'

/** The days an insight covers, as one short label. */
export function insightDays(insight: Pick<Insight, 'firstDay' | 'lastDay'>): string {
  return insight.firstDay === insight.lastDay
    ? longDay(insight.lastDay)
    : `${longDay(insight.firstDay)} – ${longDay(insight.lastDay)}`
}
