import { http } from '@/lib/api'

/** The event alert settings: whether members are told before an event, and how. */
export type CalendarAlertSettings = {
  on: boolean
  directMessage: boolean
  ping: boolean
  channelId: string | null
  minutesBefore: number
}

/** The fewest and most minutes before the start; the server holds the same two numbers. */
export const LEAST_MINUTES_BEFORE = 5
export const MOST_MINUTES_BEFORE = 1440

const base = '/api/calendar/alerts'

export const calendarAlertApi = {
  get: () => http.request<CalendarAlertSettings>(base),
  set: (settings: CalendarAlertSettings) => http.put<CalendarAlertSettings>(base, settings),
}
