// The sample evening the copy of the Live page plays through. Pure and deterministic, so the page
// renders the same on the server and in the browser, and so it can be tested.
//
// All names here are made up. None of it is a real group, world or person.

export type Standing = 'Ordinary' | 'Member' | 'Staff' | 'Flagged'

export interface SamplePerson {
  id: string
  name: string
  standing: Standing
  /** Shown beside a flagged person on the Live page. */
  flag?: string
}

export interface SampleWorld {
  id: string
  name: string
  author: string
  capacity: number
  /** Two colours standing in for the world's picture. */
  picture: [string, string]
}

export interface Presence {
  personId: string
  /** Minutes after the sample evening began. */
  arrivedAt: number
}

export interface RoomState {
  id: string
  worldId: string
  instance: string
  access: 'Group members' | 'Members and friends' | 'Anyone'
  region: string
  openedAt: number
  /** Moderator person ids whose companion is in the room. */
  watching: string[]
  people: Presence[]
  /** From VRChat's own count. Equal to people.length while somebody is watching. */
  headCount: number
  peak: number
}

export interface LiveState {
  minute: number
  tick: number
  rooms: RoomState[]
  /** The person who arrived on the latest tick, for the row highlight. */
  lastArrival: string | null
}

export type SampleEvent =
  | { kind: 'arrive'; room: number; person: string }
  | { kind: 'leave'; room: number; person: string }
  | { kind: 'count'; room: number; by: number }

/** Where the clock reads 21:00 on the sample evening. */
export const EVENING_START = 21 * 60

export const PEOPLE: SamplePerson[] = [
  { id: 'usr_oto', name: 'Oto', standing: 'Staff' },
  { id: 'usr_wren', name: 'Wren', standing: 'Staff' },
  { id: 'usr_mossfox', name: 'mossfox', standing: 'Member' },
  { id: 'usr_juniper', name: 'Juniper.exe', standing: 'Member' },
  { id: 'usr_kiri', name: 'Kiri', standing: 'Ordinary' },
  { id: 'usr_teaspoon', name: 'TeaSpoon', standing: 'Flagged', flag: '2 prior actions' },
  { id: 'usr_pebble', name: 'pebble', standing: 'Member' },
  { id: 'usr_sable', name: 'Sable', standing: 'Ordinary' },
  { id: 'usr_novadrift', name: 'NovaDrift', standing: 'Member' },
  { id: 'usr_lumen', name: 'lumen_', standing: 'Ordinary' },
  { id: 'usr_haze', name: 'Haze', standing: 'Member' },
]

export const WORLDS: SampleWorld[] = [
  { id: 'wrld_harbor', name: 'Lantern Harbor', author: 'saltwater', capacity: 40, picture: ['#f6a45c', '#5b4bd6'] },
  { id: 'wrld_orbit', name: 'Quiet Orbit Lounge', author: 'moonbase', capacity: 32, picture: ['#2a78d6', '#1baf7a'] },
  { id: 'wrld_karaoke', name: 'Pixel Karaoke Hall', author: 'bitcrush', capacity: 24, picture: ['#e87ba4', '#eda100'] },
]

export const GROUP_NAME = 'Lantern Social'

export function initialLive(): LiveState {
  return {
    minute: 104,
    tick: 0,
    lastArrival: null,
    rooms: [
      {
        id: 'room_1',
        worldId: 'wrld_harbor',
        instance: '48213',
        access: 'Members and friends',
        region: 'us',
        openedAt: 0,
        watching: ['usr_oto'],
        people: [
          { personId: 'usr_oto', arrivedAt: 2 },
          { personId: 'usr_mossfox', arrivedAt: 11 },
          { personId: 'usr_juniper', arrivedAt: 37 },
          { personId: 'usr_kiri', arrivedAt: 62 },
          { personId: 'usr_pebble', arrivedAt: 88 },
        ],
        headCount: 5,
        peak: 9,
      },
      {
        id: 'room_2',
        worldId: 'wrld_orbit',
        instance: '07731',
        access: 'Group members',
        region: 'eu',
        openedAt: 41,
        watching: [],
        people: [],
        headCount: 3,
        peak: 6,
      },
    ],
  }
}

/**
 * One loop of the evening. Every arrival has a matching departure and the counts sum to zero, so
 * after a full loop the rooms hold the same people they started with.
 */
export const EVENTS: SampleEvent[] = [
  { kind: 'arrive', room: 0, person: 'usr_teaspoon' },
  { kind: 'count', room: 1, by: 1 },
  { kind: 'arrive', room: 0, person: 'usr_novadrift' },
  { kind: 'leave', room: 0, person: 'usr_kiri' },
  { kind: 'count', room: 1, by: 1 },
  { kind: 'arrive', room: 0, person: 'usr_sable' },
  { kind: 'leave', room: 0, person: 'usr_teaspoon' },
  { kind: 'count', room: 1, by: -1 },
  { kind: 'arrive', room: 0, person: 'usr_kiri' },
  { kind: 'leave', room: 0, person: 'usr_novadrift' },
  { kind: 'count', room: 1, by: -1 },
  { kind: 'leave', room: 0, person: 'usr_sable' },
]

export function step(state: LiveState): LiveState {
  const event = EVENTS[state.tick % EVENTS.length]
  const minute = state.minute + 1 + (state.tick % 3)
  let lastArrival: string | null = null

  const rooms = state.rooms.map((room, index) => {
    if (index !== event.room) return room

    switch (event.kind) {
      case 'arrive': {
        if (room.people.some((p) => p.personId === event.person)) return room
        lastArrival = event.person
        const people = [...room.people, { personId: event.person, arrivedAt: minute }]
        return { ...room, people, headCount: people.length, peak: Math.max(room.peak, people.length) }
      }
      case 'leave': {
        const people = room.people.filter((p) => p.personId !== event.person)
        return { ...room, people, headCount: room.watching.length > 0 ? people.length : room.headCount }
      }
      case 'count': {
        const headCount = Math.max(0, room.headCount + event.by)
        return { ...room, headCount, peak: Math.max(room.peak, headCount) }
      }
    }
  })

  return { minute, tick: state.tick + 1, rooms, lastArrival }
}

/** "22:44" for a minute of the sample evening. */
export function clock(minute: number): string {
  const total = (EVENING_START + minute) % (24 * 60)
  const h = Math.floor(total / 60)
  const m = total % 60
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`
}

/** "1h 44m" for a stretch of minutes, as the app and the Discord card write it. */
export function duration(minutes: number): string {
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

export const personById = (id: string): SamplePerson =>
  PEOPLE.find((p) => p.id === id) ?? { id, name: id, standing: 'Ordinary' }

export const worldById = (id: string): SampleWorld => WORLDS.find((w) => w.id === id) ?? WORLDS[0]
