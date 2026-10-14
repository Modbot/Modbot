import { http } from '@/lib/api'

/** Somebody who picked an option, by the name the server shows. */
export type Voter = { discordUserId: string; name: string }

export type VoteOption = {
  id: string
  label: string
  count: number
  /** Who picked it; null on an anonymous vote. */
  voters: Voter[] | null
}

export type Vote = {
  id: string
  question: string
  channelId: string
  roleId: string | null
  showCounts: boolean
  anonymous: boolean
  status: 'open' | 'closed'
  createdAt: string
  closedAt: string | null
  total: number
  options: VoteOption[]
  /** Set on the answer to a close: why Discord's message still shows the buttons. */
  messageProblem: string | null
}

export type VoteList = {
  votes: Vote[]
  /** Whether this person may post and close votes. */
  canManage: boolean
  mostOptions: number
  mostQuestionLength: number
  mostLabelLength: number
}

export type NewVote = {
  question: string
  channelId: string
  roleId: string | null
  options: string[]
  showCounts: boolean
  anonymous: boolean
}

export const voteApi = {
  list: () => http.request<VoteList>('/api/votes'),
  create: (body: NewVote) => http.post<Vote>('/api/votes', body),
  close: (id: string) => http.post<Vote>(`/api/votes/${encodeURIComponent(id)}/close`, {}),
}

/** The options a person typed, one to a line, without the blank lines. */
export function optionLines(typed: string): string[] {
  return typed
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}
