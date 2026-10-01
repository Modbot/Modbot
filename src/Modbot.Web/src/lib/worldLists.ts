import { http } from '@/lib/api'
import type { CalendarEvent, CalendarWorld } from '@/lib/calendar'

/** One world in a list, and the players its game is for (world lists design). */
export type WorldListWorld = {
  worldId: string
  /** Null until Modbot has read the world. */
  name: string | null
  thumbnailUrl: string | null
  minPlayers: number | null
  maxPlayers: number | null
}

/** An event that picks its world from a list. */
export type WorldListUse = { eventId: string; title: string; state: string }

export type WorldList = {
  id: string
  name: string
  worlds: WorldListWorld[]
  usedBy: WorldListUse[]
  createdAt: string
  updatedAt: string
}

export type WorldLists = { lists: WorldList[]; canManage: boolean }

export type WorldListInput = {
  name: string
  worlds: { worldId: string; minPlayers: number | null; maxPlayers: number | null }[]
}

/** A world picked as the next game during an event. */
export type NextGameWorld = {
  worldId: string
  name: string | null
  thumbnailUrl: string | null
  minPlayers: number | null
  maxPlayers: number | null
  pickedAt: string
}

/** Next game during an event that picks from a world list (world lists design §6). */
export type NextGame = {
  eventId: string
  eventTitle: string
  listId: string
  listName: string
  instanceId: string | null
  /** People in the event's instance now; null when unknown, and then players are ignored. */
  people: number | null
  peopleUnsure: boolean
  game: NextGameWorld | null
  /** The last pick found nothing that fits. */
  noneFits: boolean
  canPick: boolean
}

const base = '/api/world-lists'

export const worldListApi = {
  all: () => http.request<WorldLists>(base),
  search: (text: string) => http.request<CalendarWorld[]>(`${base}/worlds?search=${encodeURIComponent(text)}`),
  /** A world's link or id; a world Modbot has never read is read from VRChat once. */
  find: (text: string) => http.post<CalendarWorld>(`${base}/worlds/find`, { text }),
  create: (body: WorldListInput) => http.post<WorldList>(base, body),
  update: (id: string, body: WorldListInput) => http.put<WorldList>(`${base}/${encodeURIComponent(id)}`, body),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
}

const calendarBase = '/api/calendar'

export const worldPickApi = {
  pickAgain: (eventId: string) => http.post<CalendarEvent>(`${calendarBase}/events/${encodeURIComponent(eventId)}/pick-again`),
  nextGame: (eventId: string) => http.request<NextGame>(`${calendarBase}/events/${encodeURIComponent(eventId)}/next-game`),
  /** Picks the next game; with `instead`, puts that world back and picks the one after it. */
  pickNextGame: (eventId: string, instanceId: string | null, instead: string | null) =>
    http.post<NextGame>(`${calendarBase}/events/${encodeURIComponent(eventId)}/next-game`, { instanceId, instead }),
  /** Next game for the open event an instance belongs to; nothing when it belongs to none. */
  forInstance: (instanceId: string) =>
    http.request<NextGame | undefined>(`${calendarBase}/next-game?instanceId=${encodeURIComponent(instanceId)}`),
}

/** "2–8", "2+", "up to 8", or null when the world fits any number. */
export function playersText(min: number | null, max: number | null): string | null {
  if (min !== null && max !== null) return min === max ? `${min}` : `${min}–${max}`
  if (min !== null) return `${min}+`
  if (max !== null) return `up to ${max}`
  return null
}

/** The world's page on vrchat.com. */
export function vrchatWorldUrl(worldId: string): string {
  return `https://vrchat.com/home/world/${encodeURIComponent(worldId)}`
}
