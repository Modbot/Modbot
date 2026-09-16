/*
 * The shapes and the small pieces of arithmetic behind /rooms, apart from the page so they can be
 * tested without a browser.
 */

export type Room = {
  location: string
  worldId: string
  worldName?: string | null
  worldImageUrl?: string | null
  joinLink?: string | null
  region?: string | null
  openedAt: string
}

export type Group = {
  groupId: string
  groupName?: string | null
  groupIconUrl?: string | null
  groupBannerUrl?: string | null
  groupUrl: string
  reportedAt: string
  rooms: Room[]
}

/** The regions VRChat names, in words. Anything else is shown as it arrived, in capitals. */
const REGIONS: Record<string, string> = {
  us: 'US West',
  use: 'US East',
  usw: 'US West',
  eu: 'Europe',
  jp: 'Japan',
}

export function regionName(region?: string | null): string | null {
  if (!region) return null
  return REGIONS[region.toLowerCase()] ?? region.toUpperCase()
}

/**
 * How long the room has been open, in the largest unit that is still a whole number. Empty for a
 * time that cannot be read, so a bad value shows nothing rather than "NaN min".
 */
export function openFor(openedAt: string, now: number): string {
  const started = Date.parse(openedAt)
  if (Number.isNaN(started)) return ''

  const minutes = Math.floor((now - started) / 60000)
  if (minutes < 1) return 'Just now'
  if (minutes < 60) return `${minutes} min`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} hr`

  return `${Math.floor(hours / 24)} d`
}

/** Groups with rooms open first, then by name. A group with nothing open is still listed. */
export function order(groups: Group[]): Group[] {
  return [...groups].sort((a, b) => {
    if ((a.rooms.length > 0) !== (b.rooms.length > 0)) return a.rooms.length > 0 ? -1 : 1
    return (a.groupName ?? a.groupId).localeCompare(b.groupName ?? b.groupId)
  })
}
