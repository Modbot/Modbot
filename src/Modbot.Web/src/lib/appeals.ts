import { http } from '@/lib/api'
import { formatDay } from '@/lib/format'

/** How an appeal stands: still waiting, or answered one way. */
export type AppealOutcome = 'pending' | 'upheld' | 'turned-down'

/** One appeal staff wrote down. */
export type Appeal = {
  id: string
  userId: string
  displayName: string | null
  /** The day it reached the team, "2026-10-13" (a UTC day). */
  receivedOn: string
  outcome: AppealOutcome
  caseFileId: string | null
  caseFileDate: string | null
  note: string
  loggedAt: string
  loggedByName: string
  decidedAt: string | null
  decidedByName: string | null
  /** Whether this person may uphold or turn it down: it waits, and they may lift bans. */
  canDecide: boolean
}

export type AppealList = {
  appeals: Appeal[]
  total: number
  waiting: number
  canLog: boolean
  now: string
}

export type AppealQuery = {
  userId?: string
  year?: number
  outcome?: AppealOutcome
  limit?: number
}

export type LogAppealInput = {
  userId: string
  receivedOn: string | null
  caseFileId: string | null
  note: string | null
}

/** One number on the yearly report. */
export type ReportLine = { key: string; label: string; count: number }

export type YearlyReport = {
  year: number
  lines: ReportLine[]
  /** Newest first. */
  years: number[]
  /** The first day anything is counted, "2026-03-01"; a year that starts before it is a partial year. */
  countedSince: string | null
  now: string
}

/** The longest note the server keeps. */
export const APPEAL_NOTE_MAX = 1000

const base = '/api/appeals'

export const appealApi = {
  list: (query: AppealQuery = {}) => {
    const params = new URLSearchParams()
    if (query.userId) params.set('userId', query.userId)
    if (query.year !== undefined) params.set('year', String(query.year))
    if (query.outcome) params.set('outcome', query.outcome)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    const text = params.toString()
    return http.request<AppealList>(text ? `${base}?${text}` : base)
  },
  log: (body: LogAppealInput) => http.post<Appeal>(base, body),
  decide: (id: string, outcome: 'upheld' | 'turned-down') =>
    http.post<Appeal>(`${base}/${encodeURIComponent(id)}/decide`, { outcome }),
  report: (year?: number) => http.request<YearlyReport>(year === undefined ? `${base}/report` : `${base}/report?year=${year}`),
  /** The address that downloads the report as a CSV file. */
  csvHref: (year: number) => `${base}/report.csv?year=${year}`,
}

export const OUTCOME_LABEL: Record<AppealOutcome, string> = {
  pending: 'Waiting',
  upheld: 'Upheld',
  'turned-down': 'Turned down',
}

/**
 * A day-only value ("2026-10-13") as "Oct 13", with the year when it is not this year.
 *
 * Read as noon on the viewer's own calendar, not midnight UTC: a day with no time of day has no
 * zone, and reading it as an instant in a zone behind UTC would show the day before.
 */
export function dayOnly(day: string): string {
  return formatDay(`${day}T12:00:00`)
}

/** The name an appeal shows for the person, falling back to the id. */
export function appealPerson(appeal: Appeal): string {
  return appeal.displayName?.trim() || appeal.userId
}

/** Today as a day-only value in UTC, for a form's date limit. */
export function todayUtc(now: string): string {
  return now.slice(0, 10)
}
