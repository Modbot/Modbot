// The words and small decisions the Lists page is made of, and nothing that talks to the server,
// so the Node test runner can load this file as it is -- the split `giveawayRules.ts` uses.

/** How many people a list holds, as the page says it. */
export type ListCount = { count: number; fromPolledData: boolean }

/**
 * The number of people in a list, honest about where it came from.
 *
 * A list whose rules read presence reports counts people near a line that was sampled, not
 * measured, so the count is "about 312" there and "312" everywhere else (M7 §2.3) -- the pattern
 * the giveaway preview uses.
 */
export function countWords({ count, fromPolledData }: ListCount): string {
  const n = count.toLocaleString()
  return fromPolledData ? `about ${n}` : n
}

/** "1 person", "about 312 people". */
export function peopleWords(list: ListCount): string {
  return `${countWords(list)} ${list.count === 1 ? 'person' : 'people'}`
}

/**
 * The file name a server's `Content-Disposition` names, or the fallback.
 *
 * ASP.NET writes both `filename=` and `filename*=UTF-8''…`; the plain one is enough here, because
 * the server builds the name from letters, digits and dashes only.
 */
export function fileNameFrom(disposition: string | null, fallback: string): string {
  if (!disposition) return fallback
  const match = /filename="?([^";]+)"?/i.exec(disposition)
  return match?.[1]?.trim() || fallback
}

/** The two files a list can be exported as. */
export const EXPORT_FORMATS = ['csv', 'json'] as const

export type ExportFormat = (typeof EXPORT_FORMATS)[number]

export const EXPORT_LABEL: Record<ExportFormat, string> = {
  csv: 'CSV',
  json: 'JSON',
}
