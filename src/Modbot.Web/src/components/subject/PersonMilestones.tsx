import { useEffect, useState } from 'react'
import { Panel } from '@/components/subject/shared'
import { api, type PersonMilestones as Milestones } from '@/lib/api'
import { formatDay, ordinal, plural } from '@/lib/format'

/**
 * What the member milestones know about a person: how many events they came to, the date of the
 * event that counted as their first and of the one that made them a Regular, and when they came
 * back after a long time away. Counted on the server from the instances where a moderator's
 * companion saw them, whatever the Discord settings say.
 *
 * Hidden while there is nothing to show -- no event came to and no return -- and when the read
 * fails, because it is a mark beside the visits and the visits already say so when they cannot load.
 */
export function PersonMilestones({ userId }: { userId: string }) {
  const [marks, setMarks] = useState<Milestones | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .personMilestones(userId)
      .then((next) => {
        if (!cancelled) setMarks(next)
      })
      .catch(() => {
        if (!cancelled) setMarks(null)
      })
    return () => {
      cancelled = true
    }
  }, [userId])

  if (!marks || (marks.eventsCame === 0 && !marks.backAt)) return null

  const lines = [
    marks.eventsCame > 0 ? `Came to ${marks.eventsCame} ${plural(marks.eventsCame, 'event')}` : null,
    marks.firstDate ? `${ordinal(marks.firstAt)} event on ${formatDay(marks.firstDate)}` : null,
    marks.regularSince ? `Regular since ${formatDay(marks.regularSince)}` : null,
    marks.backAt && marks.backAfterDays
      ? `Back after ${marks.backAfterDays} ${plural(marks.backAfterDays, 'day')} on ${formatDay(marks.backAt)}`
      : null,
  ].filter((line): line is string => !!line)

  return (
    <Panel title="Milestones">
      <ul className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        {lines.map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>
    </Panel>
  )
}
