import { http } from '@/lib/api'
import type { AvailabilityCell } from '@/lib/availabilityZones'
import type { TeamPerson } from '@/lib/availabilityTeam'

/** A person's own week as saved: the zone it is in and the hours they are free (availability design §3). */
export type MyAvailability = {
  /** Null until they have saved once. */
  timeZone: string | null
  cells: AvailabilityCell[]
  savedAt: string | null
}

/** Everyone who may enter their times, and their weeks, each on that person's own clock. */
export type TeamAvailability = { people: TeamPerson[] }

const base = '/api/availability'

export const availabilityApi = {
  mine: () => http.request<MyAvailability>(`${base}/mine`),
  /** Replaces the whole week. */
  save: (timeZone: string, cells: AvailabilityCell[]) => http.put<MyAvailability>(`${base}/mine`, { timeZone, cells }),
  team: () => http.request<TeamAvailability>(base),
}
