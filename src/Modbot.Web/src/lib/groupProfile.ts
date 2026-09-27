// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
import type { GroupInfo, GroupProfileEdit, JoinState } from './api.ts'
import { vrchatLanguages } from './vrchatTags.ts'

/**
 * The pieces of editing the group's VRChat profile that are worth a test: VRChat's limits, the
 * checks made before a Save is sent, what counts as a change, and where the About card remembers
 * being folded.
 *
 * The limits are the ones VRChat documents (its API description's `UpdateGroupRequest`), the same
 * numbers the server checks against (`GroupPageRules`). The rules have no documented limit.
 */
export const LIMITS = {
  nameMin: 3,
  nameMax: 64,
  descriptionMax: 250,
  languages: 3,
  links: 3,
} as const

/** Who can join, in VRChat's order, with the words the page shows. */
export const JOIN_STATES: readonly { value: JoinState; label: string }[] = [
  { value: 'open', label: 'Anyone can join' },
  { value: 'request', label: 'Anyone can ask to join' },
  { value: 'invite', label: 'Invite only' },
  { value: 'closed', label: 'Closed' },
]

export function joinStateLabel(state: JoinState | null): string {
  return JOIN_STATES.find((s) => s.value === state)?.label ?? '—'
}

/** What is wrong with a name, or null. Counted after trimming, as the server counts it. */
export function nameProblem(name: string): string | null {
  const length = name.trim().length
  if (length < LIMITS.nameMin) return `At least ${LIMITS.nameMin} characters.`
  if (length > LIMITS.nameMax) return `At most ${LIMITS.nameMax} characters.`
  return null
}

export function descriptionProblem(description: string): string | null {
  return description.trim().length > LIMITS.descriptionMax ? `At most ${LIMITS.descriptionMax} characters.` : null
}

/** A link as the server will store it, or null when it is not a web address. */
export function webLink(text: string): string | null {
  try {
    const url = new URL(text.trim())
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}

/** The links typed, cleaned: blanks dropped, repeats dropped, each written as the server stores it. */
export function tidyLinks(typed: readonly string[]): string[] {
  const kept: string[] = []
  for (const text of typed) {
    if (!text.trim()) continue
    const link = webLink(text) ?? text.trim()
    if (!kept.includes(link)) kept.push(link)
  }
  return kept
}

/** What is wrong with a list of links, or null. */
export function linksProblem(typed: readonly string[]): string | null {
  const bad = typed.find((t) => t.trim() && !webLink(t))
  if (bad !== undefined) return `“${bad.trim()}” is not a web address. Links start with https://.`
  if (tidyLinks(typed).length > LIMITS.links) return `At most ${LIMITS.links} links.`
  return null
}

export function languagesProblem(codes: readonly string[]): string | null {
  return codes.length > LIMITS.languages ? `At most ${LIMITS.languages} languages.` : null
}

/** The languages a picker offers next: every one VRChat lists, less those already chosen. */
export function languageChoices(chosen: readonly string[]): { code: string; name: string }[] {
  const taken = new Set(chosen.map((c) => c.toLowerCase()))
  return vrchatLanguages().filter((l) => !taken.has(l.code))
}

/** The profile as the editor holds it: every field as text or a list, never null. */
export type ProfileDraft = {
  name: string
  description: string
  rules: string
  languages: string[]
  links: string[]
  joinState: JoinState | null
}

export function draftFrom(info: GroupInfo): ProfileDraft {
  return {
    name: info.name ?? '',
    description: info.description ?? '',
    rules: info.rules ?? '',
    languages: [...info.languages],
    links: [...info.links],
    joinState: info.joinState,
  }
}

function sameList(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i])
}

/**
 * The fields of a draft that differ from the group as stored, as the edit a Save sends. Empty when
 * nothing changed, and then nothing is sent at all.
 */
export function profileChanges(info: GroupInfo, draft: ProfileDraft): GroupProfileEdit {
  const edit: GroupProfileEdit = {}

  if (draft.name.trim() !== (info.name ?? '')) edit.name = draft.name.trim()
  if (draft.description.trim() !== (info.description ?? '')) edit.description = draft.description.trim()
  if (draft.rules.trim() !== (info.rules ?? '')) edit.rules = draft.rules.trim()
  if (!sameList(draft.languages, info.languages)) edit.languages = [...draft.languages]

  const links = tidyLinks(draft.links)
  if (!sameList(links, info.links)) edit.links = links

  if (draft.joinState && draft.joinState !== info.joinState) edit.joinState = draft.joinState

  return edit
}

export function isEmptyEdit(edit: GroupProfileEdit): boolean {
  return Object.keys(edit).length === 0
}

/** The first thing wrong with a draft, or null when it can be sent. */
export function draftProblem(draft: ProfileDraft): string | null {
  return (
    nameProblem(draft.name) ??
    descriptionProblem(draft.description) ??
    languagesProblem(draft.languages) ??
    linksProblem(draft.links)
  )
}
