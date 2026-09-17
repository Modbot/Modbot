/**
 * VRChat's trust ranks: the name a nameplate shows and the colour VRChat paints it.
 *
 * The rank comes from the server as the enum member's name (`KnownUser`), never as a number, and
 * the server is the only place it is computed from tags. This file only knows how to show one.
 * The tag table and where each colour comes from: `.agent/research/2026-09-16-vrchat-trust-ranks.md`.
 */

export type TrustRank =
  | 'Visitor'
  | 'NewUser'
  | 'User'
  | 'KnownUser'
  | 'TrustedUser'
  | 'Legend'
  | 'Nuisance'
  | 'VRChatTeam'

const RANKS: Record<TrustRank, { label: string; colour: string }> = {
  Visitor: { label: 'Visitor', colour: '#CCCCCC' },
  NewUser: { label: 'New User', colour: '#1778FF' },
  User: { label: 'User', colour: '#2BCF5C' },
  KnownUser: { label: 'Known User', colour: '#FF7B42' },
  TrustedUser: { label: 'Trusted User', colour: '#8143E6' },
  Legend: { label: 'Legend', colour: '#FFD000' },
  Nuisance: { label: 'Nuisance', colour: '#782F2F' },
  VRChatTeam: { label: 'VRChat Team', colour: '#FF2626' },
}

/** The rank a server value names, or null for anything this build does not know. */
export function trustRank(value: unknown): TrustRank | null {
  return typeof value === 'string' && value in RANKS ? (value as TrustRank) : null
}

export function trustRankLabel(rank: TrustRank): string {
  return RANKS[rank].label
}

export function trustRankColour(rank: TrustRank): string {
  return RANKS[rank].colour
}
