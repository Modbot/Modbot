import { useEffect, useState } from 'react'
import { AuditLogList } from '@/components/audit/AuditLogList'
import { useFilters, type FilterChip } from '@/lib/filters'
import { AUDIT_DEFAULTS } from '@/lib/pageFilters'
import { useLocation } from '@/lib/router'
import { api, type AuditEntry } from '@/lib/api'

const NO_FILTERS: FilterChip[] = []
const AUDIT_STARTS = [AUDIT_DEFAULTS]
// Opened at one entry, the log starts with no chips, and Clear goes back there so the entry stays on the list.
const FACT_STARTS = [NO_FILTERS, AUDIT_DEFAULTS]

/**
 * The Audit log page: the merged timeline (spec 5.9.5) with its chips in the address.
 *
 * The list itself is `components/audit/AuditLogList`, which an instance's popup shows too,
 * narrowed to that instance. What is the page's alone is where its chips live -- the address, so
 * a link reproduces the view -- and `?fact=`, which opens the log at one entry.
 */
export function AuditLog() {
  const [location] = useLocation()

  // `?fact=` opens the log at one entry: the timeline starts there and the row is marked. It is
  // what a source chip under a Chat answer links to.
  const factId = location.search.get('fact')

  const [fetched, setFetched] = useState<{ factId: string; entry: AuditEntry } | null>(null)

  // Only the entry that was fetched for the fact currently in the address counts, so nothing has
  // to be cleared when the address changes.
  const openAt = fetched?.factId === factId ? fetched.entry : null

  // Opened at one entry, by a link that says nothing about filters: none, so that entry is on the
  // list whatever it is. A case file's link to its ban, a Chat answer's source chip.
  const [chips, setChips] = useFilters(AUDIT_DEFAULTS, factId ? NO_FILTERS : undefined)
  const starts = factId ? FACT_STARTS : AUDIT_STARTS

  useEffect(() => {
    if (!factId) return

    let cancelled = false
    api
      .auditEntry(factId)
      .then((entry) => {
        if (!cancelled) setFetched({ factId, entry })
      })
      .catch(() => {
        // An entry this account may not read, or one that is gone: the log opens where it always does.
      })

    return () => {
      cancelled = true
    }
  }, [factId])

  return (
    <AuditLogList chips={chips} starts={starts} onChange={setChips} factId={factId} openAt={openAt} place="page" />
  )
}
