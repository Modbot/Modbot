import { http } from '@/lib/api'

/** One automatic role rule, as the server keeps it. */
export type AutomaticRole = {
  id: string
  name: string | null
  vrChatRoleId: string | null
  discordRoleId: string | null
  wholeTeam: boolean
  whoDiscordRoleIds: string[]
  whoVRChatRoleIds: string[]
  companionRunning: boolean
  clipsReady: boolean
  inGroupInstance: boolean
  minimumLevel: boolean
  minimumLevelPassedTrial: boolean
  minimumLevelStaffRoleId: string | null
  overturnedBans: boolean
  overturnedBansMax: number | null
  overturnedBansDays: number | null
  dailyBanCap: boolean
  dailyBanCapMax: number | null
  missedClips: boolean
  missedClipsMax: number | null
  missedClipsDays: number | null
  takeBackUserIds: string[]
  createdAt: string
  updatedAt: string
}

/** What is sent to add or change a rule: the same fields, without the id and the dates. */
export type AutomaticRoleInput = Omit<AutomaticRole, 'id' | 'createdAt' | 'updatedAt'>

/** Which actions ask the moderator's companion for a clip. */
export type ClipRequests = { bans: boolean; kicks: boolean; warnings: boolean }

/** One condition of a rule and where the account stands on it. The numbers are null for a yes-or-no condition. */
export type RuleCondition = {
  label: string
  met: boolean
  reason: string | null
  count: number | null
  max: number | null
  days: number | null
}

/** A role a rule gives, and whether the signed-in account holds it now. */
export type MyRoleStanding = { id: string; name: string; held: boolean }

/** A rule that applies to the signed-in account. */
export type MyAutomaticRole = {
  id: string
  name: string | null
  vrChatRole: MyRoleStanding | null
  discordRole: MyRoleStanding | null
  met: boolean
  conditions: RuleCondition[]
}

/** Who put a role on a person, as far as Modbot can tell. */
export type AutomaticRoleGiven = 'ByModbot' | 'ByHand' | 'NotHeld'

/** One Modbot account and one of the roles a rule gives. */
export type AutomaticRoleHolder = {
  userId: string
  username: string
  platform: string
  roleName: string
  given: AutomaticRoleGiven
  ruleMet: boolean
  conditions: RuleCondition[]
}

const base = '/api/settings/automatic-roles'

export const automaticRolesApi = {
  list: () => http.request<{ rules: AutomaticRole[] }>(base),
  holders: (id: string) => http.request<{ holders: AutomaticRoleHolder[] }>(`${base}/${id}/holders`),
  mine: () => http.request<{ rules: MyAutomaticRole[] }>('/api/me/automatic-roles'),
  add: (body: AutomaticRoleInput) => http.post<AutomaticRole>(base, body),
  change: (id: string, body: AutomaticRoleInput) => http.put<AutomaticRole>(`${base}/${id}`, body),
  remove: (id: string) => http.del<void>(`${base}/${id}`),
  clipRequests: () => http.request<ClipRequests>('/api/settings/clip-requests'),
  setClipRequests: (body: ClipRequests) => http.put<ClipRequests>('/api/settings/clip-requests', body),
}

/** A new rule: nothing switched on, the numbers a switch starts with once it is. */
export function blankRule(): AutomaticRoleInput {
  return {
    name: null,
    vrChatRoleId: null,
    discordRoleId: null,
    wholeTeam: true,
    whoDiscordRoleIds: [],
    whoVRChatRoleIds: [],
    companionRunning: false,
    clipsReady: false,
    inGroupInstance: false,
    minimumLevel: false,
    minimumLevelPassedTrial: true,
    minimumLevelStaffRoleId: null,
    overturnedBans: false,
    overturnedBansMax: 1,
    overturnedBansDays: 30,
    dailyBanCap: false,
    dailyBanCapMax: 5,
    missedClips: false,
    missedClipsMax: 0,
    missedClipsDays: 7,
    takeBackUserIds: [],
  }
}

/** A saved rule as the form starts: its own fields, a switched-off number put back to its starting value. */
export function ruleInputOf(saved: AutomaticRole): AutomaticRoleInput {
  const blank = blankRule()
  const { id: _id, createdAt: _created, updatedAt: _updated, ...rule } = saved

  return {
    ...rule,
    overturnedBansMax: rule.overturnedBansMax ?? blank.overturnedBansMax,
    overturnedBansDays: rule.overturnedBansDays ?? blank.overturnedBansDays,
    dailyBanCapMax: rule.dailyBanCapMax ?? blank.dailyBanCapMax,
    missedClipsMax: rule.missedClipsMax ?? blank.missedClipsMax,
    missedClipsDays: rule.missedClipsDays ?? blank.missedClipsDays,
  }
}

/** The switched-on conditions as short labels, with their numbers. */
export function conditionLabels(rule: AutomaticRole): string[] {
  const labels: string[] = []

  if (rule.companionRunning) labels.push('Companion running')
  if (rule.clipsReady) labels.push('Clips ready')
  if (rule.inGroupInstance) labels.push('In a group instance')
  if (rule.minimumLevel) labels.push('Minimum level')
  if (rule.overturnedBans)
    labels.push(`Overturned bans: at most ${rule.overturnedBansMax} in ${rule.overturnedBansDays} days`)
  if (rule.dailyBanCap) labels.push(`Daily ban cap: ${rule.dailyBanCapMax}`)
  if (rule.missedClips)
    labels.push(`Missing clips: at most ${rule.missedClipsMax} in ${rule.missedClipsDays} days`)

  return labels
}
