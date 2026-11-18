// The words and small decisions the Feedback page is made of, and nothing that talks to the
// server, so the Node test runner can load this file as it is -- the split `listWords.ts` uses.

/** What sort of thing staff sent. */
export type FeedbackKind = 'bug' | 'idea' | 'change'

/** Where an item stands with the admins. */
export type FeedbackStatus = 'new' | 'reviewed' | 'done' | 'dropped'

export const FEEDBACK_KINDS: { value: FeedbackKind; label: string }[] = [
  { value: 'bug', label: 'Bug' },
  { value: 'idea', label: 'Idea' },
  { value: 'change', label: 'Change' },
]

export const FEEDBACK_STATUSES: { value: FeedbackStatus; label: string }[] = [
  { value: 'new', label: 'New' },
  { value: 'reviewed', label: 'Reviewed' },
  { value: 'done', label: 'Done' },
  { value: 'dropped', label: 'Dropped' },
]

export function feedbackKindLabel(kind: string): string {
  return FEEDBACK_KINDS.find((k) => k.value === kind)?.label ?? kind
}

export function feedbackStatusLabel(status: string): string {
  return FEEDBACK_STATUSES.find((s) => s.value === status)?.label ?? status
}

/** The badge colour of a status: new stands out, done is calm, the rest are quiet. */
export function feedbackStatusBadge(status: string): 'warn' | 'info' | 'ok' | 'outline' {
  switch (status) {
    case 'new':
      return 'warn'
    case 'reviewed':
      return 'info'
    case 'done':
      return 'ok'
    default:
      return 'outline'
  }
}

/** The list's filter: every item, or one status. */
export type FeedbackFilter = 'all' | FeedbackStatus

/** The query the list is read with. Nothing for "all", so the address stays plain. */
export function feedbackQuery(filter: FeedbackFilter): string {
  return filter === 'all' ? '' : `?status=${filter}`
}
