import { http } from '@/lib/api'

/** The event's own state (calendar design §2.1). */
export type CalendarEventState = 'draft' | 'scheduled' | 'open' | 'finished' | 'cancelled'

export type CalendarPlaceName = 'vrchat' | 'discordEvent' | 'channelPost'

export type CalendarPlaceState = 'waiting' | 'published' | 'failed' | 'removed'

export type CalendarRepeat = 'none' | 'daily' | 'weekly' | 'monthly'

export type CalendarPlace = {
  place: CalendarPlaceName
  state: CalendarPlaceState
  error: string | null
  errorAt: string | null
  updatedAt: string
}

export type CalendarOpening = {
  occurrenceStartsAt: string
  attemptedAt: string
  instanceId: string | null
  joinLink: string | null
  closed: boolean
  error: string | null
}

export type CalendarOccurrence = { startsAt: string; endsAt: string }

export type CalendarEvent = {
  id: string
  title: string
  description: string
  startsAt: string
  endsAt: string
  startsAtLocal: string
  endsAtLocal: string
  timeZone: string
  repeat: CalendarRepeat
  repeatDays: string[]
  repeatUntil: string | null
  worldId: string | null
  worldName: string | null
  worldThumbnailUrl: string | null
  accessType: string
  region: string
  imageUrl: string | null
  vrChatImageId: string | null
  category: string
  languages: string[]
  platforms: string[]
  tags: string[]
  visibility: string
  notifyMembers: boolean
  publishToVRChat: boolean
  publishToDiscord: boolean
  postToChannel: boolean
  channelId: string | null
  autoOpen: boolean
  openMinutesBefore: number
  state: CalendarEventState
  occurrenceStartsAt: string | null
  occurrenceEndsAt: string | null
  version: number
  createdAt: string
  updatedAt: string
  places: CalendarPlace[]
  opening: CalendarOpening | null
  occurrences: CalendarOccurrence[]
}

export type CalendarView = {
  events: CalendarEvent[]
  canManage: boolean
  categories: string[]
  platforms: string[]
  now: string
}

export type CalendarWorld = { worldId: string; name: string | null; thumbnailUrl: string | null }

export type CalendarFeed = { path: string | null; url: string | null }

export type CalendarEventInput = {
  title: string
  description: string
  startsAt: string
  endsAt: string
  timeZone: string
  repeat: CalendarRepeat
  repeatDays: string[]
  repeatUntil: string | null
  worldId: string | null
  accessType: string
  region: string
  imageUrl: string | null
  vrChatImageId: string | null
  category: string
  languages: string[]
  platforms: string[]
  tags: string[]
  visibility: string
  notifyMembers: boolean
  publishToVRChat: boolean
  publishToDiscord: boolean
  postToChannel: boolean
  channelId: string | null
  autoOpen: boolean
  openMinutesBefore: number
  draft: boolean
}

const base = '/api/calendar'

export const calendarApi = {
  view: (from: Date, to: Date) =>
    http.request<CalendarView>(
      `${base}?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`,
    ),
  event: (id: string) => http.request<CalendarEvent>(`${base}/events/${encodeURIComponent(id)}`),
  create: (body: CalendarEventInput) => http.post<CalendarEvent>(`${base}/events`, body),
  update: (id: string, body: CalendarEventInput) => http.put<CalendarEvent>(`${base}/events/${id}`, body),
  cancel: (id: string) => http.post<void>(`${base}/events/${id}/cancel`),
  remove: (id: string) => http.del<void>(`${base}/events/${id}`),
  worlds: () => http.request<CalendarWorld[]>(`${base}/worlds`),
  feed: () => http.request<CalendarFeed>(`${base}/feed`),
  regenerateFeed: () => http.post<CalendarFeed>(`${base}/feed`),
}

export const STATE_LABEL: Record<CalendarEventState, string> = {
  draft: 'Draft',
  scheduled: 'Scheduled',
  open: 'Open',
  finished: 'Finished',
  cancelled: 'Cancelled',
}

export const PLACE_LABEL: Record<CalendarPlaceName, string> = {
  vrchat: 'VRChat calendar',
  discordEvent: 'Discord event',
  channelPost: 'Channel post',
}

export const PLACE_STATE_LABEL: Record<CalendarPlaceState, string> = {
  waiting: 'Waiting',
  published: 'Published',
  failed: 'Failed',
  removed: 'Removed',
}

export const CATEGORY_LABEL: Record<string, string> = {
  arts: 'Arts',
  avatars: 'Avatars',
  dance: 'Dance',
  education: 'Education',
  exploration: 'Exploration',
  film_media: 'Film & media',
  gaming: 'Gaming',
  hangout: 'Hangout',
  music: 'Music',
  other: 'Other',
  performance: 'Performance',
  roleplaying: 'Roleplaying',
  wellness: 'Wellness',
}

export const PLATFORM_LABEL: Record<string, string> = {
  standalonewindows: 'PC',
  android: 'Android',
  ios: 'iOS',
}

export const DAYS: { value: string; label: string }[] = [
  { value: 'MO', label: 'Mon' },
  { value: 'TU', label: 'Tue' },
  { value: 'WE', label: 'Wed' },
  { value: 'TH', label: 'Thu' },
  { value: 'FR', label: 'Fri' },
  { value: 'SA', label: 'Sat' },
  { value: 'SU', label: 'Sun' },
]

/** `2026-09-20T20:00` for a Date, in the browser's own time — what a datetime-local input holds. */
export function localInputValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** An input for a new event: the next whole hour, two hours long, in the browser's time zone. */
export function blankEvent(now: Date): CalendarEventInput {
  const start = new Date(now)
  start.setMinutes(0, 0, 0)
  start.setHours(start.getHours() + 1)
  const end = new Date(start)
  end.setHours(end.getHours() + 2)

  return {
    title: '',
    description: '',
    startsAt: localInputValue(start),
    endsAt: localInputValue(end),
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
    repeat: 'none',
    repeatDays: [],
    repeatUntil: null,
    worldId: null,
    accessType: 'members',
    region: 'us',
    imageUrl: null,
    vrChatImageId: null,
    category: 'hangout',
    languages: [],
    platforms: [],
    tags: [],
    visibility: 'group',
    notifyMembers: false,
    publishToVRChat: true,
    publishToDiscord: false,
    postToChannel: false,
    channelId: null,
    autoOpen: false,
    openMinutesBefore: 10,
    draft: false,
  }
}

/** The input that edits an existing event. */
export function inputFrom(event: CalendarEvent): CalendarEventInput {
  return {
    title: event.title,
    description: event.description,
    startsAt: event.startsAtLocal,
    endsAt: event.endsAtLocal,
    timeZone: event.timeZone,
    repeat: event.repeat,
    repeatDays: event.repeatDays,
    repeatUntil: event.repeatUntil,
    worldId: event.worldId,
    accessType: event.accessType,
    region: event.region,
    imageUrl: event.imageUrl,
    vrChatImageId: event.vrChatImageId,
    category: event.category,
    languages: event.languages,
    platforms: event.platforms,
    tags: event.tags,
    visibility: event.visibility,
    notifyMembers: event.notifyMembers,
    publishToVRChat: event.publishToVRChat,
    publishToDiscord: event.publishToDiscord,
    postToChannel: event.postToChannel,
    channelId: event.channelId,
    autoOpen: event.autoOpen,
    openMinutesBefore: event.openMinutesBefore,
    draft: event.state === 'draft',
  }
}
