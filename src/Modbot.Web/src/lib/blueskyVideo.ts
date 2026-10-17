import type { BlueskyVideo } from './calendar.ts'

/** The largest video a post keeps: the server's `VideoFormats.MaxBytes`. */
export const BLUESKY_VIDEO_MAX_BYTES = 50 * 1024 * 1024

/** The types a video file may have; the server tells them apart by their first bytes. */
export const BLUESKY_VIDEO_TYPES = ['video/mp4', 'video/mpeg', 'video/webm', 'video/quicktime']

/** What the form sends to take a saved video away. */
export const NO_VIDEO: BlueskyVideo = { videoId: null, url: null, alt: '' }

/** Whether the post has a video: a kept file or a link. */
export function hasVideo(video: BlueskyVideo | null | undefined): boolean {
  return Boolean(video?.videoId || video?.url)
}

/** A video that is a file kept by Modbot, as `POST /api/calendar/bluesky-video` answered. */
export function videoFromFile(videoId: string): BlueskyVideo {
  return { videoId, url: null, alt: '' }
}

/** A video that is a link, fetched as the post goes out. */
export function videoFromLink(url: string): BlueskyVideo {
  return { videoId: null, url: url.trim(), alt: '' }
}

/** `video` with another alt text. */
export function videoWithAlt(video: BlueskyVideo, alt: string): BlueskyVideo {
  return { ...video, alt }
}

/** Why a file may not be the video, or null: the same sentences the server says. */
export function videoFileProblem(file: { size: number; type: string }): string | null {
  if (file.size > BLUESKY_VIDEO_MAX_BYTES) return 'The video is larger than 50 MB.'
  // A file the browser gave no type is left to the server, which tells a video by its first bytes.
  if (file.type !== '' && !BLUESKY_VIDEO_TYPES.includes(file.type)) return 'The video must be an MP4, MPEG, WebM or QuickTime file.'
  return null
}

/** Why a link may not be the video, or null: it must be an https link with no user name or password. */
export function videoLinkProblem(url: string): string | null {
  const asked = url.trim()
  try {
    const link = new URL(asked)
    if (link.protocol !== 'https:' || link.username || link.password) return 'The video link must start with https://.'
    return null
  } catch {
    return 'The video link must start with https://.'
  }
}

/** The host a link video shows as, or the link itself when it cannot be read. */
export function videoHost(url: string): string {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}
