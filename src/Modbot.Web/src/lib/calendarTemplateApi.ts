import { http } from '@/lib/api'
import type { CalendarTemplate, CalendarTemplateFields, CalendarTemplates } from '@/lib/calendarTemplates'

const base = '/api/calendar/templates'

export const calendarTemplateApi = {
  all: () => http.request<CalendarTemplates>(base),
  create: (name: string, fields: CalendarTemplateFields) => http.post<CalendarTemplate>(base, { name, fields }),
  update: (id: string, name: string, fields: CalendarTemplateFields) =>
    http.put<CalendarTemplate>(`${base}/${encodeURIComponent(id)}`, { name, fields }),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
}
