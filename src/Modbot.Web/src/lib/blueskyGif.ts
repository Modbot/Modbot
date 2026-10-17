import type { BlueskyGif } from './calendar.ts'
import type { KlipyGif } from './klipy.ts'

/** What the form sends to take a saved GIF away. */
export const NO_GIF: BlueskyGif = { url: null, alt: '' }

/** Whether the post has a GIF. */
export function hasGif(gif: BlueskyGif | null | undefined): boolean {
  return Boolean(gif?.url)
}

/** A picked GIF: its address, and its title as the alt text. */
export function gifFromKlipy(picked: Pick<KlipyGif, 'url' | 'title'>): BlueskyGif {
  return { url: picked.url, alt: picked.title }
}

/** `gif` with another alt text. */
export function gifWithAlt(gif: BlueskyGif, alt: string): BlueskyGif {
  return { ...gif, alt }
}
