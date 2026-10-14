import { http } from '@/lib/api'

/**
 * A category made in Modbot, with how many events carry it. `blueskyTag` is the tag Bluesky posts of its
 * events end with, without the `#`, empty for none; `blueskyTagIsOwn` is false while it follows the name.
 */
export type CalendarCategoryRow = { id: string; name: string; usedBy: number; blueskyTag: string; blueskyTagIsOwn: boolean }

/** The longest a Bluesky tag may be, in characters as a person counts them; Bluesky takes no more. */
export const MAX_TAG = 64

const segmenter = new Intl.Segmenter()

/** The tag a name gives, as the server makes it: its words in CamelCase ("Dance night" becomes `DanceNight`). Empty for none. */
export function tagFromName(name: string): string {
  let tag = ''
  let newWord = true

  for (const letter of name) {
    if (!/[\p{L}\p{Nd}\p{M}]/u.test(letter)) {
      newWord = true
      continue
    }

    tag += newWord ? letter.toUpperCase() : letter
    newWord = false
  }

  const long = [...segmenter.segment(tag)].length > MAX_TAG
  return tag === '' || /^\p{Nd}+$/u.test(tag) || long ? '' : tag
}

export type CalendarCategories = { categories: CalendarCategoryRow[]; canManage: boolean }

const base = '/api/calendar/categories'

export const calendarCategoryApi = {
  all: () => http.request<CalendarCategories>(base),
  /** `blueskyTag` null follows the name; an empty one is no tag. */
  create: (name: string, blueskyTag: string | null) => http.post<CalendarCategoryRow>(base, { name, blueskyTag }),
  rename: (id: string, name: string, blueskyTag: string | null) =>
    http.put<CalendarCategoryRow>(`${base}/${encodeURIComponent(id)}`, { name, blueskyTag }),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
}
