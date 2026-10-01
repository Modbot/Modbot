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
  /** What Discord said when the post for the first person was refused. */
  firstJoinDiscordPostError?: string | null
  /** What VRChat said when the group post for the first person was refused. */
  firstJoinVRChatPostError?: string | null
  /** VRChat gave no clear answer, and Modbot is looking for an instance it may have made. */
  checking?: boolean
  /** VRChat made the instance, whether or not Modbot has it on record yet. */
  instanceMade?: boolean
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
/** How far the current time's invites have got (calendar auto-invite design §10). */
export type CalendarInvites = {
  /** The M in "Invited N of M": everybody on the queue who was not skipped. */
  total: number
  /** The N: VRChat invites and Discord messages that went out. */
  invited: number
  vrChat: number
  discord: number
  couldNotReach: number
  noWay: number
  waiting: number
  stopped: number
  skipped: number
}

export type CalendarStaffChoice = { id: string; name: string; hasVRChat: boolean; hasDiscord: boolean }

export type CalendarListChoice = { id: string; name: string }

/** Who an event can invite. `lists` is null without See members and See profiles. */
export type CalendarInviteChoices = { staff: CalendarStaffChoice[]; lists: CalendarListChoice[] | null }


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
  inviteHostUserId?: string | null
  inviteStaffUserIds?: string[] | null
  inviteListId?: string | null
  /** Null when it has no list, or the list is gone. */
  inviteListName?: string | null
  announceFirstJoinInDiscord?: boolean
  announceFirstJoinInVRChat?: boolean
  /** Null before any invites were queued for the current time. */
  invites?: CalendarInvites | null
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

/** How long before a time's start Open now is offered: the most an event may open early. */
export const OPEN_NOW_EARLIEST_MINUTES = 120

/**
 * Whether Open now is offered: the event is on, has a world, its current or next time is near or
 * running, and no instance of that time is open or opening.
 */
export function canOpenNow(event: CalendarEvent, now: Date): boolean {
  if (event.state !== 'scheduled' && event.state !== 'open') return false
  if (!event.worldId || !event.occurrenceStartsAt || !event.occurrenceEndsAt) return false

  const start = Date.parse(event.occurrenceStartsAt)
  const end = Date.parse(event.occurrenceEndsAt)
  if (now.getTime() < start - OPEN_NOW_EARLIEST_MINUTES * 60_000 || now.getTime() >= end) return false

  const opening = event.opening
  if (!opening) return true
  if (opening.instanceId) return opening.closed
  // Made, but not on record as an instance: the server would call it open.
  if (opening.instanceMade) return false
  // VRChat may have made one; a second request could make two.
  if (opening.checking) return false
  // A failed attempt, or one with no answer for a minute.
  return !!opening.error || now.getTime() - Date.parse(opening.attemptedAt) >= 60_000
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
  /** Whether the form may upload a VRChat picture (Settings). Missing from an older server: taken as off. */
  pictureUploads?: boolean
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
  inviteHostUserId: string | null
  inviteStaffUserIds: string[]
  inviteListId: string | null
  announceFirstJoinInDiscord: boolean
  announceFirstJoinInVRChat: boolean
}

const base = '/api/calendar'

/** The pictures VRChat's calendar picture may be. Checked again by the server, from the bytes. */
export const VRCHAT_PICTURE_TYPES = ['image/png', 'image/jpeg']

/** Where a person gets VRChat+, which VRChat's gallery upload needs on the account Modbot signs in as. */
export const VRCHAT_PLUS_URL = 'https://hello.vrchat.com/vrchatplus'

/**
 * Whether VRChat refused an upload for want of the permission to use the gallery tag (its words:
 * "You don't have permission to use tag: gallery."), which is what an account without VRChat+ gets.
 */
export function isGalleryRefusal(message: string): boolean {
  return /permission to use tag/i.test(message)
}

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
  /** Opens the instance for the event's current or next time now. Answers with the event. */
  openNow: (id: string) => http.post<CalendarEvent>(`${base}/events/${id}/open`),
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
  inviteChoices: () => http.request<CalendarInviteChoices>(`${base}/invite-choices`),
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
    inviteHostUserId: null,
    inviteStaffUserIds: [],
    inviteListId: null,
    announceFirstJoinInDiscord: false,
    announceFirstJoinInVRChat: false,
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
    inviteHostUserId: event.inviteHostUserId ?? null,
    inviteStaffUserIds: event.inviteStaffUserIds ?? [],
    inviteListId: event.inviteListId ?? null,
    announceFirstJoinInDiscord: event.announceFirstJoinInDiscord ?? false,
    announceFirstJoinInVRChat: event.announceFirstJoinInVRChat ?? false,
  }
}
