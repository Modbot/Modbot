import { http } from '@/lib/api'

/** The limits the server enforces at save; the boxes stop at the same numbers. */
export const TERM_LIMITS = { name: 100, otherNames: 10, otherName: 50, meaning: 1000, terms: 500 } as const
export const RULE_LIMITS = { title: 100, text: 1500, rules: 100 } as const

/** A word the community uses, as staff typed it. */
export type Term = {
  id: string
  name: string
  otherNames: string[]
  meaning: string
  updatedAt: string
}

export type Terms = { terms: Term[]; canEdit: boolean }

export type TermInput = { name: string; otherNames: string[]; meaning: string }

/** One of the community's rules. The number is its place in the list. */
export type Rule = {
  id: string
  number: number
  title: string
  text: string
  updatedAt: string
}

export type Rules = { rules: Rule[]; canEdit: boolean }

export type RuleInput = { title: string; text: string }

const terms = '/api/terms'
const rules = '/api/rules'

export const termApi = {
  all: () => http.request<Terms>(terms),
  create: (body: TermInput) => http.post<Term>(terms, body),
  update: (id: string, body: TermInput) => http.put<Term>(`${terms}/${encodeURIComponent(id)}`, body),
  remove: (id: string) => http.del<void>(`${terms}/${encodeURIComponent(id)}`),
}

export const ruleApi = {
  all: () => http.request<Rules>(rules),
  create: (body: RuleInput) => http.post<Rule>(rules, body),
  update: (id: string, body: RuleInput) => http.put<Rule>(`${rules}/${encodeURIComponent(id)}`, body),
  move: (id: string, direction: 'up' | 'down') =>
    http.post<Rules>(`${rules}/${encodeURIComponent(id)}/move`, { direction }),
  remove: (id: string) => http.del<void>(`${rules}/${encodeURIComponent(id)}`),
}
