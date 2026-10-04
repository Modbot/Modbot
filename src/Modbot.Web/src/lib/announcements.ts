import { http, type MissingGroupPermission } from '@/lib/api'

/** Where a message to everyone in an instance is: as the server keeps it. */
export type AnnouncementState = 'scheduled' | 'sending' | 'sent' | 'refused' | 'failed' | 'cancelled'

export type Announcement = {
  id: string
  instanceId: string
  title: string
  message: string
  state: AnnouncementState
  sendAt: string
  timeZone: string
  sentAt: string | null
  /** VRChat's HTTP status, when it answered. */
  status: number | null
  /** VRChat's own words for a refusal, or Modbot's for a failure. */
  error: string | null
  missingGroupPermission: MissingGroupPermission | null
  createdAt: string
}

export type AnnouncementList = { announcements: Announcement[]; canSend: boolean }

export type AnnouncementInput = {
  instanceId: string
  title: string
  message: string
  when: 'now' | 'later'
  /** `yyyy-MM-ddTHH:mm`, as a datetime-local box gives it. */
  sendAt: string
  timeZone: string
}

/** The server's caps (VRChatAnnouncement.MaxTitleLength and MaxMessageLength). */
export const ANNOUNCEMENT_TITLE_MAX = 100
export const ANNOUNCEMENT_MESSAGE_MAX = 500

/** The word a row shows for its state. */
export const ANNOUNCEMENT_STATE_WORDS: Record<AnnouncementState, string> = {
  scheduled: 'Scheduled',
  sending: 'Sending',
  sent: 'Sent',
  refused: 'Refused',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const announcementsApi = {
  list: () => http.request<AnnouncementList>('/api/announcements'),
  send: (input: AnnouncementInput) => http.post<Announcement>('/api/announcements', input),
  cancel: (id: string) => http.post<Announcement>(`/api/announcements/${encodeURIComponent(id)}/cancel`),
}
