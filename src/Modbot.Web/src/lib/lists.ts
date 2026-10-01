import { ApiError, http } from '@/lib/api'
import type { GiveawayBuilder, GiveawayRule } from '@/lib/giveaways'
import { fileNameFrom, type ExportFormat } from './listWords.ts'

/** What would change if a list changed. */
export type ListUse = {
  giveaways: string[]
  autoInvites: boolean
  /** Titles of events still being run that invite the list. */
  events?: string[] | null
}

/** A saved list: a name and the rules that decide who is in it (lists design). */
export type SavedList = {
  id: string
  name: string
  rules: GiveawayRule
  ruleLines: string[]
  createdBy: string | null
  createdAt: string
  updatedAt: string
  usedBy: ListUse
}

export type SavedLists = {
  lists: SavedList[]
  canManage: boolean
}

export type ListPerson = {
  key: string
  vrChatUserId: string | null
  discordUserId: string | null
  name: string | null
  inGroup: boolean
  inDiscord: boolean
  linked: boolean
  fromPolledData: boolean
  closeCall: boolean
}

/** Who is in a list right now, a page at a time. */
export type ListPeople = {
  count: number
  considered: number
  closeCalls: number
  fromPolledData: boolean
  unanswerable: string | null
  stopped: boolean
  countedAt: string
  people: ListPerson[]
  page: number
  pageSize: number
}

export type ListInput = { name: string; rules: GiveawayRule }

const base = '/api/lists'

export const listApi = {
  all: () => http.request<SavedLists>(base),
  builder: () => http.request<GiveawayBuilder>(`${base}/builder`),
  people: (id: string, page: number, pageSize = 50) =>
    http.request<ListPeople>(`${base}/${encodeURIComponent(id)}/people?page=${page}&pageSize=${pageSize}`),
  preview: (rules: GiveawayRule) => http.post<ListPeople>(`${base}/preview`, { rules }),
  create: (body: ListInput) => http.post<SavedList>(base, body),
  update: (id: string, body: ListInput) => http.put<SavedList>(`${base}/${encodeURIComponent(id)}`, body),
  remove: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
  exportFile,
}

/**
 * Asks for a list's people as a file and hands it to the browser to save.
 *
 * Not `http.post`: the answer is a file rather than JSON. A POST rather than a link, so nothing
 * but a person pressing the button -- no prefetch, no link preview -- ever makes one, and each
 * one is recorded on the server as it is made.
 */
async function exportFile(id: string, format: ExportFormat, fallbackName: string): Promise<void> {
  let response: Response

  try {
    response = await fetch(`${base}/${encodeURIComponent(id)}/export`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ format }),
      credentials: 'same-origin',
    })
  } catch {
    throw new ApiError(0, 'Could not reach the Modbot server. Is it still running?', null)
  }

  if (!response.ok) {
    let message = `The server answered ${response.status}.`
    try {
      const body: unknown = JSON.parse(await response.text())
      if (typeof body === 'object' && body !== null && 'error' in body) message = String((body as { error: unknown }).error)
    } catch {
      // Not JSON; the status is all there is to say.
    }
    throw new ApiError(response.status, message, null)
  }

  const blob = await response.blob()
  const url = URL.createObjectURL(blob)

  try {
    const link = document.createElement('a')
    link.href = url
    link.download = fileNameFrom(response.headers.get('content-disposition'), `${fallbackName}.${format}`)
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    // Late enough for the browser to have started the download from it.
    setTimeout(() => URL.revokeObjectURL(url), 10_000)
  }
}
