// Nothing here talks to the server or the page, so the Node test runner can load this file as it is
// (the API calls are in `calendarTemplateApi.ts`).
import type { CalendarEventInput } from './calendar.ts'
import { DEFAULT_BLUESKY_BEFORE } from './calendarPlaces.ts'

/**
 * Event templates (calendar templates plan): a name and everything the event form holds except its
 * dates. The fields are the event input's own, so the form loads one as it is; the dates are the
 * length, and a repeat is its pattern only.
 */
export type CalendarTemplateFields = Omit<
  CalendarEventInput,
  | 'startsAt'
  | 'endsAt'
  | 'timeZone'
  | 'repeatUntil'
  | 'draft'
  | 'featured'
  | 'vrChatTitle'
  | 'vrChatDescription'
  | 'discordTitle'
  | 'discordDescription'
  | 'googleTitle'
  | 'googleDescription'
  | 'blueskyTitle'
  | 'blueskyDescription'
  | 'categoryIds'
> & {
  /** How long the event lasts, 1 minute to 7 days. */
  lengthMinutes: number
  /** Null leaves it to the event. */
  featured: boolean | null
  /** Each site's own title and description; null says the shared one. */
  vrChatTitle: string | null
  vrChatDescription: string | null
  discordTitle: string | null
  discordDescription: string | null
  googleTitle: string | null
  googleDescription: string | null
  blueskyTitle: string | null
  blueskyDescription: string | null
  /** The categories made in Modbot; null for a template saved before they were kept. */
  categoryIds: string[] | null
}

export type CalendarTemplate = {
  id: string
  name: string
  fields: CalendarTemplateFields
  /** The account that made it, by user name; null when that account is gone. */
  createdBy: string | null
  createdAt: string
  updatedAt: string
}

export type CalendarTemplates = {
  templates: CalendarTemplate[]
  /** The caller holds Manage templates. */
  canManage: boolean
}

/** The longest an event may last: 7 days, the server's `CalendarEndpoints.MaxLength`. */
export const MAX_LENGTH_MINUTES = 7 * 24 * 60

/** What the length box starts at. */
export const FIRST_LENGTH_MINUTES = 120

/**
 * The event form's input for a template: the template's fields over `base`, which holds the dates
 * and the time zone the form needs. A template says nothing about a last date, so none is set.
 */
export function inputFromTemplate(fields: CalendarTemplateFields, base: CalendarEventInput): CalendarEventInput {
  const { lengthMinutes: _length, ...rest } = fields

  return {
    ...base,
    ...rest,
    description: fields.description ?? '',
    repeat: fields.repeat ?? 'none',
    repeatDays: fields.repeatDays ?? [],
    repeatEvery: fields.repeatEvery ?? 1,
    repeatTimes: fields.repeatTimes ?? null,
    repeatUntil: null,
    featured: fields.featured ?? false,
    openMinutesBefore: fields.openMinutesBefore ?? 10,
    draft: false,
    // Left out, a new event ticks Google Calendar itself once it is set up.
    publishToGoogle: fields.publishToGoogle ?? null,
    vrChatTitle: fields.vrChatTitle ?? '',
    vrChatDescription: fields.vrChatDescription ?? '',
    discordTitle: fields.discordTitle ?? '',
    discordDescription: fields.discordDescription ?? '',
    googleTitle: fields.googleTitle ?? '',
    googleDescription: fields.googleDescription ?? '',
    // A template saved before Bluesky has neither: off, and the usual time.
    publishToBluesky: fields.publishToBluesky ?? false,
    blueskyPostBeforeMinutes: fields.blueskyPostBeforeMinutes ?? DEFAULT_BLUESKY_BEFORE,
    blueskyTitle: fields.blueskyTitle ?? '',
    blueskyDescription: fields.blueskyDescription ?? '',
    blueskyPictures: fields.blueskyPictures ?? null,
    blueskyVideo: fields.blueskyVideo ?? null,
    blueskyGif: fields.blueskyGif ?? null,
    // A template saved before who can reply, quote posts and languages: anyone, quotes allowed, no language.
    blueskyWhoCanReply: fields.blueskyWhoCanReply?.length ? fields.blueskyWhoCanReply : ['anyone'],
    blueskyQuotesOff: fields.blueskyQuotesOff ?? false,
    blueskyLanguages: fields.blueskyLanguages ?? [],
    // A template saved before post colours has none: Modbot's violet.
    postColour: fields.postColour ?? null,
    categoryIds: fields.categoryIds ?? [],
  }
}

/** The template's fields for what the form holds: no dates, no last date, and the length. */
export function fieldsFromInput(input: CalendarEventInput, lengthMinutes: number): CalendarTemplateFields {
  const { startsAt: _starts, endsAt: _ends, timeZone: _zone, repeatUntil: _until, draft: _draft, ...rest } = input

  return {
    ...rest,
    lengthMinutes,
    featured: input.featured,
    worldListId: input.worldListId ?? null,
    vrChatTitle: input.vrChatTitle ?? null,
    vrChatDescription: input.vrChatDescription ?? null,
    discordTitle: input.discordTitle ?? null,
    discordDescription: input.discordDescription ?? null,
    googleTitle: input.googleTitle ?? null,
    googleDescription: input.googleDescription ?? null,
    blueskyTitle: input.blueskyTitle ?? null,
    blueskyDescription: input.blueskyDescription ?? null,
    categoryIds: input.categoryIds,
  }
}

/** `2026-10-20T19:00` and 90 minutes on: `2026-10-20T20:30`. Wall-clock arithmetic, no zone. */
export function addMinutes(local: string, minutes: number): string {
  const at = new Date(`${local}:00Z`)
  at.setUTCMinutes(at.getUTCMinutes() + minutes)
  return at.toISOString().slice(0, 16)
}

/** How long a new event runs from `startsAt` to `endsAt` (both `2026-10-20T19:00`), kept within 1 minute to 7 days. */
export function lengthBetween(startsAt: string, endsAt: string): number {
  const minutes = Math.round((Date.parse(`${endsAt}:00Z`) - Date.parse(`${startsAt}:00Z`)) / 60000)
  return Number.isFinite(minutes) ? Math.min(Math.max(minutes, 1), MAX_LENGTH_MINUTES) : FIRST_LENGTH_MINUTES
}

/** The input a template starts a new event with: its fields, and the end its length gives after the start already there. */
export function newEventFromTemplate(fields: CalendarTemplateFields, base: CalendarEventInput): CalendarEventInput {
  const loaded = inputFromTemplate(fields, base)
  return { ...loaded, endsAt: addMinutes(loaded.startsAt, fields.lengthMinutes) }
}

/** A length as whole hours and the minutes over, for the two boxes. */
export function splitLength(minutes: number): { hours: number; minutes: number } {
  return { hours: Math.floor(minutes / 60), minutes: minutes % 60 }
}

/** The length the two boxes hold. */
export function joinLength(hours: number, minutes: number): number {
  return Math.max(0, Math.round(hours)) * 60 + Math.max(0, Math.round(minutes))
}

/** A length in words: "2 h", "1 h 30 min", "45 min", "3 days". */
export function lengthWords(minutes: number): string {
  if (minutes >= 24 * 60 && minutes % (24 * 60) === 0) {
    const days = minutes / (24 * 60)
    return days === 1 ? '1 day' : `${days} days`
  }

  const { hours, minutes: rest } = splitLength(minutes)
  if (hours === 0) return `${rest} min`
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`
}
