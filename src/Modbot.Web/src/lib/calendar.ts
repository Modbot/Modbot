import type { CalendarReady } from './calendarPlaces.ts'
import { http, type InstanceRow, type MissingGroupPermission, type PersonSeen, type PlaceCounts } from '@/lib/api'

/** The event's own state (calendar design §2.1). */
export type CalendarEventState = 'draft' | 'scheduled' | 'open' | 'finished' | 'cancelled'

/** `cancelPost` is the message a cancel posts in the channel when it was ticked. */
export type CalendarPlaceName = 'vrchat' | 'discordEvent' | 'channelPost' | 'cancelPost'

export type CalendarPlaceState = 'waiting' | 'published' | 'failed' | 'removed'

export type CalendarRepeat = 'none' | 'daily' | 'weekly' | 'monthly'

export type CalendarPlace = {
  place: CalendarPlaceName
  state: CalendarPlaceState
  error: string | null
  errorAt: string | null
  updatedAt: string
  missingGroupPermission?: MissingGroupPermission | null
  /** VRChat only: VRChat gave no answer to adding it and does not have it. Sent again only by `tryVRChatAgain`. */
  canTryAgain?: boolean
}

export type CalendarOpening = {
  occurrenceStartsAt: string
  attemptedAt: string
  instanceId: string | null
  joinLink: string | null
  closed: boolean
  error: string | null
}

/** One date of an event (calendar design §2.2). */
export type CalendarOccurrence = {
  startsAt: string
  endsAt: string
  /** When the repeat says this date starts; what a change to this one date names it by. */
  plannedStartsAt: string
  /** The date's own title, when it was given one. */
  title?: string | null
  /** The date's own description, when it was given one. */
  description?: string | null
  /** What VRChat said when it refused this date's own change. */
  vrChatError?: string | null
}

/** A change to one date of a repeating event: its times and, when they differ, its own words. */
export type CalendarDateInput = {
  plannedStartsAt: string
  startsAt: string
  endsAt: string
  title: string | null
  description: string | null
}

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
  /** Made on VRChat (on vrchat.com or in the game) and read in by Modbot. */
  madeOnVRChat: boolean
  places: CalendarPlace[]
  opening: CalendarOpening | null
  occurrences: CalendarOccurrence[]
  /** When it was cancelled; its times after that never ran. */
  cancelledAt?: string | null
  /** The world list its world is picked from, date by date; `worldId` is then the current date's. */
  worldListId?: string | null
  worldListName?: string | null
  /** The list has no worlds, so no world can be picked for the event. */
  worldListEmpty?: boolean
  /** Dates of a repeating event cancelled on their own, at their planned times. */
  cancelledDates?: CalendarOccurrence[] | null
}

/**
 * The world to show for one time of an event. An event that picks from a list has a world only for
 * the date it is on now; its other dates show the list instead (world lists design §5).
 */
export function worldAt(event: CalendarEvent, start: Date): { worldId: string; worldName: string | null } | { listName: string } | null {
  if (event.worldListId) {
    const current = event.occurrenceStartsAt !== null && Date.parse(event.occurrenceStartsAt) === start.getTime()
    if (current && event.worldId) return { worldId: event.worldId, worldName: event.worldName }
    return { listName: event.worldListName ?? '' }
  }
  return event.worldId ? { worldId: event.worldId, worldName: event.worldName } : null
}

/**
 * Whether a time of an event has run, or is running, so it has results: started by `now`, not a
 * draft, and not after the event was cancelled (the server has no results for those).
 */
export function hasRun(event: CalendarEvent, start: Date, now: Date): boolean {
  if (event.state === 'draft' || start.getTime() > now.getTime()) return false
  if (event.state === 'cancelled' && event.cancelledAt && start.getTime() >= Date.parse(event.cancelledAt)) return false
  return true
}

export type CalendarView = {
  events: CalendarEvent[]
  canManage: boolean
  categories: string[]
  platforms: string[]
  now: string
  /** See analytics as well: what each time an event ran did, and Past events. */
  canSeeResults?: boolean
  /** Which places are set up (calendar design §14.3). Missing from an older server: taken as set up. */
  ready?: CalendarReady | null
}

/** The event as each place would show it, drawn by the server from the code that sends it (§14.2). */
export type CalendarPreview = {
  /** Null on a server without the Discord bot built in. */
  discordEvent: {
    name: string
    description: string | null
    startsAt: string
    endsAt: string
    location: string
    coverUrl: string | null
  } | null
  channelPost: {
    groupName: string | null
    title: string
    titleLink: string | null
    description: string | null
    colour: number
    /** Discord's own text: `<t:…:F>` times and `[name](address)` links. */
    fields: { name: string; value: string; inline: boolean }[]
    footer: string | null
    pictureUrl: string | null
    buttons: { label: string; url: string }[]
  } | null
  vrChat: {
    update: boolean
    title: string
    description: string
    startsAt: string
    endsAt: string
    category: string | null
    visibility: string | null
    languages: string[]
    platforms: string[]
    tags: string[]
    imageId: string | null
    repeat: { frequency: string; days: string[]; until: string | null; timeZone: string | null } | null
    notify: boolean
  }
  feed: {
    calendarName: string
    title: string
    notes: string | null
    location: string | null
    startsAt: string
    endsAt: string
    timeZone: string
    repeat: string | null
  }
}

/** One time an event ran, and what it did. */
export type CalendarOccurrenceResult = {
  eventId: string
  title: string
  startsAt: string
  endsAt: string
  /** The instance it ran in, with the instance popup's own figures; null when there was none. */
  instance: InstanceRow | null
  /** The instance is the one Modbot opened for this time, not one found by world and time. */
  openedByModbot: boolean
  /** Joined the group from when the event opened until a day after it ended. */
  newMembers: number
  joinRequests: number
  /** What a moderator's client saw there; null without ViewAuditLog or with no instance. */
  seen: PlaceCounts | null
}

/** The middle value of each figure over an event's earlier times. */
export type CalendarUsual = {
  times: number
  peakPeople: number | null
  minutesOpen: number | null
  newMembers: number
  joinRequests: number
  peopleSeen: number | null
  minutesSeen: number | null
}

export type CalendarResults = {
  occurrence: CalendarOccurrenceResult
  /** Longest first. Empty without ViewAuditLog. */
  people: PersonSeen[]
  usual: CalendarUsual | null
  canSeeWhoWasThere: boolean
  now: string
}

export type CalendarPast = { occurrences: CalendarOccurrenceResult[]; from: string; now: string }

export type CalendarWorld = { worldId: string; name: string | null; thumbnailUrl: string | null }

export type CalendarFeed = { path: string | null; url: string | null }

export type CalendarVRChatRead = {
  outcome: 'read' | 'remembered' | 'notConfigured' | 'waiting' | 'failed'
  error: string | null
}

/** A read of VRChat's calendar: the range a page shows, or what the next event needs. */
export type CalendarVRChatReadInput =
  | { from: Date; to: Date; refresh?: boolean }
  | { upcoming: true; refresh?: boolean }

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
  /** Pick the world from this world list, date by date, instead of `worldId`. */
  worldListId?: string | null
}

const base = '/api/calendar'

/** The pictures VRChat's calendar picture may be. Checked again by the server, from the bytes. */
export const VRCHAT_PICTURE_TYPES = ['image/png', 'image/jpeg']

/** The largest VRChat calendar picture: 10 MB, the server's `VRChatPictureUploads.MaxBytes`. */
export const VRCHAT_PICTURE_MAX_BYTES = 10 * 1024 * 1024

export const calendarApi = {
  view: (from: Date, to: Date) =>
    http.request<CalendarView>(
      `${base}?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`,
    ),
  event: (id: string) => http.request<CalendarEvent>(`${base}/events/${encodeURIComponent(id)}`),
  create: (body: CalendarEventInput) => http.post<CalendarEvent>(`${base}/events`, body),
  update: (id: string, body: CalendarEventInput) => http.put<CalendarEvent>(`${base}/events/${id}`, body),
  /** `postInChannel`: also post in the event's channel that it is cancelled, once. */
  cancel: (id: string, postInChannel = false) => http.post<void>(`${base}/events/${id}/cancel`, { postInChannel }),
  /** Sends the event to VRChat's calendar again, for a VRChat place with `canTryAgain`. */
  tryVRChatAgain: (id: string) => http.post<void>(`${base}/events/${id}/vrchat/try-again`),
  /** The form's input drawn the way each place would show it. Saves nothing. */
  preview: (eventId: string | null, input: CalendarEventInput) =>
    http.post<CalendarPreview>(`${base}/preview`, { eventId, event: input }),
  /** Moves one date of a repeating event, or gives it its own words; the other dates stay. */
  changeDate: (id: string, body: CalendarDateInput) => http.put<CalendarEvent>(`${base}/events/${id}/dates`, body),
  /** Cancels one date of a repeating event; the other dates stay. `postInChannel` as for `cancel`. */
  cancelDate: (id: string, plannedStartsAt: string, postInChannel = false) =>
    http.post<void>(`${base}/events/${id}/dates/cancel`, { plannedStartsAt, postInChannel }),
  remove: (id: string) => http.del<void>(`${base}/events/${id}`),
  worlds: () => http.request<CalendarWorld[]>(`${base}/worlds`),
  /**
   * Uploads a picture to VRChat for the event's VRChat calendar entry and answers with its file id,
   * to save as `vrChatImageId`. `eventId`: the event, when it is already saved.
   */
  uploadVRChatPicture: (picture: Blob, eventId: string | null) =>
    http.request<{ fileId: string }>(
      `${base}/vrchat-picture${eventId ? `?eventId=${encodeURIComponent(eventId)}` : ''}`,
      { method: 'POST', body: picture, headers: { 'content-type': picture.type || 'application/octet-stream' } },
    ),
  feed: () => http.request<CalendarFeed>(`${base}/feed`),
  /** What the time of an event that started at `at` did. */
  results: (id: string, at: Date) =>
    http.request<CalendarResults>(
      `${base}/events/${encodeURIComponent(id)}/results?at=${encodeURIComponent(at.toISOString())}`,
    ),
  /** Every time an event ran over the last 90 days, the most at once first. */
  past: () => http.request<CalendarPast>(`${base}/past`),
  regenerateFeed: () => http.post<CalendarFeed>(`${base}/feed`),
  /**
   * Brings events made on VRChat, and changes and deletes made there, into Modbot's calendar
   * (calendar design §12). Asks VRChat only for months not read in the last five minutes, unless
   * `refresh`; can take several seconds.
   */
  readVRChat: (input: CalendarVRChatReadInput) =>
    http.post<CalendarVRChatRead>(
      `${base}/vrchat`,
      'upcoming' in input
        ? { upcoming: true, refresh: input.refresh ?? false }
        : { from: input.from.toISOString(), to: input.to.toISOString(), upcoming: false, refresh: input.refresh ?? false },
    ),
}

/** What a read of VRChat's calendar that did not go through says, or null when it did. */
export function vrchatReadProblem(read: CalendarVRChatRead): string | null {
  if (read.outcome === 'failed') return `Could not read VRChat's calendar${read.error ? `: ${read.error}` : '.'}`
  if (read.outcome === 'waiting') return "VRChat's calendar is waiting on its rate limit."
  return null
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
  cancelPost: 'Cancelled post',
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
    publishToDiscord: true,
    postToChannel: false,
    channelId: null,
    autoOpen: false,
    openMinutesBefore: 10,
    draft: false,
  }
}

/** An input for a new event at a time drawn on the calendar, in the browser's time zone. */
export function newEventAt(title: string, start: Date, end: Date): CalendarEventInput {
  return { ...blankEvent(start), title, startsAt: localInputValue(start), endsAt: localInputValue(end) }
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
    worldListId: event.worldListId ?? null,
  }
}
