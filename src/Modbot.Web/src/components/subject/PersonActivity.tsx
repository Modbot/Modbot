import { useCallback, useMemo, useState } from 'react'
import { AuditLogList } from '@/components/audit/AuditLogList'
import { Panel } from '@/components/subject/shared'
import type { AuditEntry, PersonView } from '@/lib/api'
import type { FilterChip } from '@/lib/filters'
import type { LiveEvent } from '@/lib/liveStream'
import { PERSON_ACTIVITY_START, personChip } from '@/lib/pageFilters'
import { concernsAny, foundUnder, type PersonAsked } from '@/lib/personTimeline'

/**
 * Everything recorded about this person, a page at a time, with the audit log's own filters.
 *
 * The Audit log page's own list, as the instance popup's Activity tab is, so the two read alike and
 * a change to how the log reads shows here too. The person is pinned and never drawn as a chip; the
 * server reads every account tied to the one the popup was opened on. It starts on Everything with
 * the Show chip already in the bar, so Moderation only is one click away when arrivals and leaves
 * bury the bans. Chips changed here last until the tab is left.
 */
export function PersonActivity({ person, asked }: { person: PersonView; asked: PersonAsked }) {
  const fixed = useMemo<FilterChip[]>(() => [personChip(asked.platform, asked.id)], [asked.platform, asked.id])

  // Where the tab starts, and where Clear puts it back. No chips at all is everything as well.
  const [starts] = useState<FilterChip[][]>(() => [PERSON_ACTIVITY_START, []])
  const [chips, setChips] = useState<FilterChip[]>(starts[0])

  const concerns = useCallback((event: LiveEvent) => concernsAny(person, event), [person])
  const under = useCallback((entry: AuditEntry) => foundUnder(person, entry), [person])

  return (
    <Panel title="Recorded about this person" flush>
      <AuditLogList
        chips={chips}
        starts={starts}
        onChange={setChips}
        fixed={fixed}
        place="popup"
        showFilter
        concerns={concerns}
        foundUnder={under}
      />
    </Panel>
  )
}
