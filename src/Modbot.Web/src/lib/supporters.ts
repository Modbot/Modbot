import { http } from '@/lib/api'

/** One level supporters pay at, with the roles it gives. */
export type SupporterTier = {
  id: string
  provider: string
  name: string
  discordRoleId: string | null
  vrChatRoleId: string | null
  /** How many supporters are paid up on it now. */
  supporters: number
}

/** One supporter as the page shows them. */
export type Supporter = {
  id: string
  provider: string
  name: string
  /** Null while nobody has matched them to a Discord account. */
  discordUserId: string | null
  discordName: string | null
  tierId: string | null
  tierName: string | null
  state: 'active' | 'ended'
  lastPaidAt: string
  paidThrough: string
  /** When the roles come off if no payment comes first. */
  rolesEndAt: string
  discordRoleGiven: boolean
  vrChatRoleGiven: boolean
  vrChatLinked: boolean
  problem: string | null
  /** The code on their latest receipt, which they can give staff to be matched. */
  code: string | null
}

export type SupporterList = {
  kofiOn: boolean
  patreonOn: boolean
  botCanManageRoles: boolean
  tiers: SupporterTier[]
  supporters: Supporter[]
  groupRoles: { id: string; name: string }[]
  mostTierNameLength: number
}

export type SupporterSettings = {
  kofiOn: boolean
  /** Whether a verification token is saved; the token itself is never sent back. */
  tokenStored: boolean
  thanksText: string
  /** The address to give Ko-fi; null until the public address is set. */
  webhookAddress: string | null
  mostThanksLength: number
  patreonOn: boolean
  /** Whether a Patreon webhook secret is saved; the secret itself is never sent back. */
  patreonSecretStored: boolean
  /** The address to give Patreon; null until the public address is set. */
  patreonWebhookAddress: string | null
}

export type SupporterSettingsInput = {
  kofiOn?: boolean
  verificationToken?: string
  thanksText?: string
  patreonOn?: boolean
  patreonWebhookSecret?: string
}

/** The name of the site a supporter or a tier came from. */
export function providerLabel(provider: string): string {
  switch (provider) {
    case 'kofi':
      return 'Ko-fi'
    case 'patreon':
      return 'Patreon'
    default:
      return provider
  }
}

export const supporterApi = {
  list: () => http.request<SupporterList>('/api/supporters'),
  addTier: (body: { name: string; discordRoleId: string | null; vrChatRoleId: string | null }) =>
    http.post<SupporterList>('/api/supporters/tiers', body),
  setTierRoles: (id: string, body: { discordRoleId: string | null; vrChatRoleId: string | null }) =>
    http.put<SupporterList>(`/api/supporters/tiers/${encodeURIComponent(id)}`, body),
  deleteTier: (id: string) => http.del<SupporterList>(`/api/supporters/tiers/${encodeURIComponent(id)}`),
  link: (id: string, discordUserId: string) =>
    http.post<SupporterList>(`/api/supporters/${encodeURIComponent(id)}/link`, { discordUserId }),
  settings: () => http.request<SupporterSettings>('/api/settings/supporters'),
  setSettings: (body: SupporterSettingsInput) => http.put<SupporterSettings>('/api/settings/supporters', body),
}

/** Whether a supporter matches what staff typed: part of their name, tier or the code on their receipt. */
export function supporterMatches(supporter: Supporter, typed: string): boolean {
  const needle = typed.trim().toLowerCase()
  if (!needle) return true

  return [supporter.name, supporter.discordName, supporter.tierName, supporter.code].some((v) =>
    (v ?? '').toLowerCase().includes(needle),
  )
}
