import { http } from '@/lib/api'

/** A category made in Modbot, with how many events carry it. */
export type CalendarCategoryRow = { id: string; name: string; usedBy: number }

export type CalendarCategories = { categories: CalendarCategoryRow[]; canManage: boolean }

const base = '/api/calendar/categories'

export const calendarCategoryApi = {
  all: () => http.request<CalendarCategories>(base),
  create: (name: string) => http.post<CalendarCategoryRow>(base, { name }),
  rename: (id: string, name: string) => http.put<CalendarCategoryRow>(`${base}/${encodeURIComponent(id)}`, { name }),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
}
