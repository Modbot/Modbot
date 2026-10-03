import { ApiError, http } from '@/lib/api'
import type {
  Post,
  PostList,
  PostListName,
  PostPreview,
  PostRequest,
  PostSettings,
  PostsHealth,
} from '@/lib/postRules'

export * from '@/lib/postRules'

const base = '/api/posts'

/** The largest picture kept for a post: 8 MB, the server's `CalendarCoverPicture.MaxBytes`. */
export const POST_PICTURE_MAX_BYTES = 8 * 1024 * 1024

/** Where a post's picture is read from. */
export function pictureAddress(pictureId: string): string {
  return `${base}/pictures/${encodeURIComponent(pictureId)}`
}

export const postsApi = {
  list: (list: PostListName, page = 1) =>
    http.request<PostList>(`${base}?list=${encodeURIComponent(list)}&page=${page}`),
  post: (id: string) => http.request<Post>(`${base}/${encodeURIComponent(id)}`),
  create: (body: PostRequest) => http.post<Post>(base, body),
  update: (id: string, body: PostRequest) => http.put<Post>(`${base}/${encodeURIComponent(id)}`, body),
  sendNow: (id: string) => http.post<Post>(`${base}/${encodeURIComponent(id)}/send-now`),
  cancel: (id: string) => http.post<Post>(`${base}/${encodeURIComponent(id)}/cancel`),
  deleteDraft: (id: string) => http.del<void>(`${base}/${encodeURIComponent(id)}`),
  tryAgain: (id: string, destinationId: string) =>
    http.post<Post>(`${base}/${encodeURIComponent(id)}/destinations/${encodeURIComponent(destinationId)}/try-again`),
  editOnSite: (id: string, destinationId: string, title: string | null, text: string) =>
    http.post<Post>(`${base}/${encodeURIComponent(id)}/destinations/${encodeURIComponent(destinationId)}/edit`, { title, text }),
  deleteOnSite: (id: string, destinationId: string) =>
    http.del<Post>(`${base}/${encodeURIComponent(id)}/destinations/${encodeURIComponent(destinationId)}`),
  /** What each ticked site would be sent, and what would stop it being scheduled. Saves nothing. */
  preview: (body: PostRequest) => http.post<PostPreview>(`${base}/preview`, body),
  /** Keeps a cropped picture for a post and answers with its id, to save as `pictureId`. */
  uploadPicture: (picture: Blob) =>
    http.request<{ pictureId: string }>(`${base}/picture`, {
      method: 'POST',
      body: picture,
      headers: { 'content-type': picture.type || 'application/octet-stream' },
    }),
  /** Sends a kept picture on to VRChat for a VRChat post, and answers with VRChat's file id. Once a minute at most. */
  uploadVRChatPicture: (pictureId: string) =>
    http.post<{ imageId: string; pictureId: string }>(`${base}/vrchat-picture`, { pictureId }),
  /** The picture behind a link, fetched by Modbot so the composer can crop it. */
  pictureFromLink: async (url: string): Promise<Blob> => {
    let response: Response

    try {
      response = await fetch(`${base}/picture-link`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ url }),
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

    return response.blob()
  },
  health: () => http.request<PostsHealth>(`${base}/health`),
  settings: () => http.request<PostSettings>('/api/settings/posts'),
  setSettings: (change: Partial<PostSettings>) => http.put<PostSettings>('/api/settings/posts', change),
}
