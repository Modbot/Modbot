import { http } from '@/lib/api'
import { feedbackQuery, type FeedbackFilter, type FeedbackStatus } from '@/lib/feedbackWords'

/** One line of the Feedback page's list. */
export type FeedbackSummary = {
  id: string
  createdAt: string
  senderName: string
  kind: string
  title: string
  status: string
}

export type FeedbackList = { items: FeedbackSummary[] }

/** One item in full. */
export type Feedback = {
  id: string
  createdAt: string
  senderUserId: string
  senderName: string
  kind: string
  title: string
  details: string
  status: string
  statusChangedAt: string | null
  statusChangedBy: string | null
}

const base = '/api/feedback'

export const feedbackApi = {
  list: (filter: FeedbackFilter = 'all') => http.request<FeedbackList>(`${base}${feedbackQuery(filter)}`),
  get: (id: string) => http.request<Feedback>(`${base}/${encodeURIComponent(id)}`),
  setStatus: (id: string, status: FeedbackStatus) =>
    http.put<Feedback>(`${base}/${encodeURIComponent(id)}/status`, { status }),
}
