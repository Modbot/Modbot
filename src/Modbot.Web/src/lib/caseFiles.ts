import { useEffect, useState } from 'react'
import { api, type CaseFileLookup } from '@/lib/api'

/**
 * Which of these people already have a case file.
 *
 * One request for a whole page of rows rather than one per row: a ban list is fifty names, and
 * fifty lookups would be fifty round trips for a badge. A caller that may not read case files
 * gets an empty map and draws nothing, which is the same thing the server would enforce anyway.
 *
 * A plain hook in its own module, not in a component file: the fast-refresh boundary only holds
 * when a module exports components or values, not both.
 */
export function useCaseFiles(userIds: readonly string[], enabled: boolean) {
  // Keyed by the request it answers, so a page that changed its rows shows nothing rather than
  // the previous page's badges while the next answer is in flight.
  const [answer, setAnswer] = useState<{ key: string; rows: ReadonlyMap<string, CaseFileLookup> }>(EMPTY)

  // The ids as one string, so the effect runs when the page's people change and not when the
  // array is merely rebuilt by a render.
  const key = userIds.join(',')

  useEffect(() => {
    if (!enabled || key.length === 0) return

    let cancelled = false

    api
      .caseLookup(key.split(','))
      .then((rows) => {
        if (!cancelled) setAnswer({ key, rows: new Map(rows.map((row) => [row.userId, row])) })
      })
      .catch(() => {
        // A badge that cannot be drawn is not worth an error on a page about something else.
        if (!cancelled) setAnswer({ key, rows: new Map() })
      })

    return () => {
      cancelled = true
    }
  }, [key, enabled])

  return answer.key === key ? answer.rows : EMPTY.rows
}

const EMPTY = { key: '', rows: new Map<string, CaseFileLookup>() as ReadonlyMap<string, CaseFileLookup> }
