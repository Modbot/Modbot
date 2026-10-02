import { http } from '@/lib/api'

/** One saved list giving one Discord role (roles from lists design). */
export type ListRole = {
  id: string
  listId: string
  listName: string
  discordRoleId: string
  roleName: string | null
  enabled: boolean
  /** How many people it gave the role to and has not taken it back from. */
  given: number
  problem: string | null
  /** Since when the brake has stopped it. */
  stoppedAt: string | null
  stoppedTaking: number | null
  removalsAllowed: number | null
}

export type ListRoles = {
  on: boolean
  botCanManageRoles: boolean
  ranAt: string | null
  problem: string | null
  roles: ListRole[]
  lists: { id: string; name: string }[]
  /** Seeing who would change names people in lists, so it needs See members and See profiles. */
  canPreview: boolean
  canApply: boolean
}

export type ListRoleChange = {
  what: 'give' | 'take'
  discordUserId: string
  vrChatUserId: string | null
  name: string | null
}

/** What one list's role would do right now. */
export type ListRolePlan = {
  id: string | null
  listId: string
  listName: string
  discordRoleId: string
  roleName: string | null
  giving: number
  taking: number
  alreadyHave: number
  takenByHand: number
  noLinkedDiscord: number
  notInServer: number
  holders: number
  /** Given the role, then seen leaving the server: nothing is sent, Modbot forgets it gave it. */
  leaving: number
  takesHeld: number
  heldBecause: string | null
  stops: boolean
  lossStops: boolean
  giveStops: boolean
  problem: string | null
  changes: ListRoleChange[]
}

export type ListRolePreview = { plans: ListRolePlan[] }

const base = '/api/discord-list-roles'

export const listRoleApi = {
  get: () => http.request<ListRoles>(base),
  set: (on: boolean) => http.put<ListRoles>(base, { on }),
  add: (listId: string, discordRoleId: string) => http.post<ListRoles>(base, { listId, discordRoleId }),
  update: (id: string, enabled: boolean) => http.put<ListRoles>(`${base}/${encodeURIComponent(id)}`, { enabled }),
  remove: (id: string) => http.del<ListRoles>(`${base}/${encodeURIComponent(id)}`),
  /** One saved list role, one not saved yet, or with neither every one that is on. */
  preview: (body: { id?: string; listId?: string; discordRoleId?: string }) =>
    http.post<ListRolePreview>(`${base}/preview`, body),
  apply: (id: string, taking: number, leaving: number, giving: number) =>
    http.post<ListRoles>(`${base}/${encodeURIComponent(id)}/apply`, { taking, leaving, giving }),
}
