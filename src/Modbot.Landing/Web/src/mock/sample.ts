// What the sample popups say about each person, world and instance. Made up, like the rest of the
// sample evening, and shaped like what the app records.

import type { Source } from '@/components/SourceBadge'
import type { Subject } from './stack'

/** A piece of a fact sentence: plain words, or a name that opens its own popup. */
export type Part = string | Subject & { label: string }

export interface SampleFact {
  id: string
  source: Source
  time: string
  parts: Part[]
}

const person = (id: string, label: string): Part => ({ kind: 'person', id, label })
const world = (id: string, label: string): Part => ({ kind: 'world', id, label })
const instance = (id: string, label: string): Part => ({ kind: 'instance', id, label })

export interface PersonSheet {
  status: string
  joinedVRChat: string
  platform: string
  memberSince: string | null
  roles: string[]
  counts: { warnings: number; kicks: number; bans: number }
  facts: SampleFact[]
  metrics: { timeSeen: string; instances: number; worlds: number; arrivals: number; firstSeen: string }
}

const ordinary = (name: string, id: string, memberSince: string | null, roles: string[]): PersonSheet => ({
  status: 'Active',
  joinedVRChat: 'Mar 2023',
  platform: 'PC',
  memberSince,
  roles,
  counts: { warnings: 0, kicks: 0, bans: 0 },
  facts: [
    { id: `${id}-1`, source: 'Client', time: '22:31', parts: [person(id, name), ' joined ', instance('instance_1', 'Lantern Harbor #48213')] },
    { id: `${id}-2`, source: 'Client', time: 'Sat', parts: [person(id, name), ' left ', world('wrld_orbit', 'Quiet Orbit Lounge'), ' after 1h 12m'] },
    ...(memberSince
      ? [{ id: `${id}-3`, source: 'VRChat' as const, time: memberSince, parts: [person(id, name), ' joined the group'] }]
      : []),
  ],
  metrics: { timeSeen: '31h 20m', instances: 44, worlds: 6, arrivals: 51, firstSeen: 'Feb 2026' },
})

export function sheetFor(id: string, name: string): PersonSheet {
  if (id === 'usr_teaspoon') {
    return {
      status: 'Ask me',
      joinedVRChat: 'Aug 2025',
      platform: 'Quest',
      memberSince: null,
      roles: [],
      counts: { warnings: 1, kicks: 1, bans: 0 },
      facts: [
        { id: 't1', source: 'Client', time: '22:45', parts: [person(id, name), ' joined ', instance('instance_1', 'Lantern Harbor #48213')] },
        { id: 't2', source: 'VRChat', time: 'Sun', parts: [person(id, name), ' was kicked from ', instance('instance_1', 'Lantern Harbor #48213'), ' by ', person('usr_oto', 'Oto')] },
        { id: 't3', source: 'VRChat', time: 'Sun', parts: [person(id, name), ' was warned in ', world('wrld_karaoke', 'Pixel Karaoke Hall'), ' by ', person('usr_wren', 'Wren')] },
        { id: 't4', source: 'Sync', time: '~Fri', parts: [person(id, name), ' left the group, inferred from the member list'] },
      ],
      metrics: { timeSeen: '4h 05m', instances: 7, worlds: 3, arrivals: 9, firstSeen: 'Aug 2026' },
    }
  }

  if (id === 'usr_oto' || id === 'usr_wren') return ordinary(name, id, '3 Jan 2026', ['Moderator'])

  const member = ['usr_mossfox', 'usr_juniper', 'usr_pebble', 'usr_novadrift', 'usr_haze'].includes(id)
  return ordinary(name, id, member ? '12 Feb 2026' : null, member ? ['Regular'] : [])
}

export function instanceFacts(instanceId: string): SampleFact[] {
  if (instanceId !== 'instance_1') {
    return [{ id: 'r2-1', source: 'VRChat', time: '21:41', parts: ['Opened by ', person('usr_wren', 'Wren')] }]
  }

  return [
    { id: 'r1-1', source: 'Client', time: '22:45', parts: [person('usr_teaspoon', 'TeaSpoon'), ' arrived'] },
    { id: 'r1-2', source: 'Client', time: '22:12', parts: [person('usr_haze', 'Haze'), ' left'] },
    { id: 'r1-3', source: 'VRChat', time: '21:58', parts: [person('usr_lumen', 'lumen_'), ' was warned by ', person('usr_oto', 'Oto')] },
    { id: 'r1-4', source: 'VRChat', time: '21:00', parts: ['Opened by ', person('usr_oto', 'Oto')] },
  ]
}
