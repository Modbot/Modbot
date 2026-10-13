import { http } from '@/lib/api'

/** Where the friendship stands. */
export type PartnerStatus = 'active' | 'paused' | 'ended'

export const PARTNER_STATUSES: { value: PartnerStatus; label: string }[] = [
  { value: 'active', label: 'Active' },
  { value: 'paused', label: 'Paused' },
  { value: 'ended', label: 'Ended' },
]

export function partnerStatusLabel(status: string): string {
  return PARTNER_STATUSES.find((s) => s.value === status)?.label ?? status
}

/** A partner group, everything as staff typed it. */
export type Partner = {
  id: string
  name: string
  /** VRChat's id for the group, as typed. */
  groupId: string | null
  status: PartnerStatus
  envoy: string | null
  contacts: string[]
  /** `yyyy-MM-dd`. */
  lastCollabOn: string | null
  lastCollabNote: string | null
  notes: string | null
  createdAt: string
  updatedAt: string
}

export type Partners = { partners: Partner[] }

export type PartnerInput = {
  name: string
  groupId: string
  status: PartnerStatus
  envoy: string
  contacts: string[]
  lastCollabOn: string | null
  lastCollabNote: string
  notes: string
}

const base = '/api/partners'

export const partnerApi = {
  all: () => http.request<Partners>(base),
  create: (body: PartnerInput) => http.post<Partner>(base, body),
  update: (id: string, body: PartnerInput) => http.put<Partner>(`${base}/${encodeURIComponent(id)}`, body),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
}
