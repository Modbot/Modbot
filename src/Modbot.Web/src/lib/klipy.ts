/**
 * Klipy's GIF search for the calendar form's GIF button. Klipy's rules for an app that shows its GIFs
 * (docs.klipy.com, integration requirements): the browser makes every request and loads every GIF itself,
 * nothing of a GIF is kept but its address, and the results are shown in the order Klipy gives them.
 */

/** Where the browser asks Klipy. */
export const KLIPY_API = 'https://api.klipy.com'

/** The search box's placeholder, which Klipy requires. */
export const KLIPY_PLACEHOLDER = 'Search KLIPY'

/** How many GIFs one page asks for. */
export const KLIPY_PER_PAGE = 24

/** The sizes Klipy offers a GIF in, the one the post uses first: a middle size loads quickly in a feed. */
const POST_SIZES = ['md', 'hd', 'sm', 'xs']

/** The sizes the picker's grid shows, the small ones first. */
const GRID_SIZES = ['sm', 'xs', 'md', 'hd']

/** One result: what the grid shows, and what a pick saves. */
export type KlipyGif = {
  id: string
  /** The GIF's title, which is its alt text when it is picked. */
  title: string
  /** A small copy for the grid, loaded from Klipy. */
  previewUrl: string
  previewWidth: number
  previewHeight: number
  /** The address to save and post: the GIF's Klipy address with the size and file names Bluesky's apps read. */
  url: string
}

export type KlipyPage = { gifs: KlipyGif[]; page: number; hasNext: boolean }

type KlipyFile = { url: string; width: number; height: number }

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/** A file of Klipy's answer, or null when it is missing or has no address. */
function fileAt(sizeFiles: unknown, kind: string): KlipyFile | null {
  if (!isRecord(sizeFiles)) return null
  const file = sizeFiles[kind]
  if (!isRecord(file) || typeof file.url !== 'string' || file.url === '') return null
  return {
    url: file.url,
    width: typeof file.width === 'number' ? file.width : 0,
    height: typeof file.height === 'number' ? file.height : 0,
  }
}

/** The first size of `sizes` that has a GIF, with its other files. */
function firstSize(files: unknown, sizes: string[]): { gif: KlipyFile; sizeFiles: unknown } | null {
  if (!isRecord(files)) return null
  for (const size of sizes) {
    const gif = fileAt(files[size], 'gif')
    if (gif) return { gif, sizeFiles: files[size] }
  }
  return null
}

/** A file's name without its ending: the slug Bluesky's apps read off the address. */
export function fileSlug(url: string | undefined): string | undefined {
  if (!url) return undefined
  const name = url.split('?')[0].split('/').pop()
  if (!name) return undefined
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(0, dot) : undefined
}

/**
 * The address a post carries for a GIF: Klipy's own address for the GIF, as given, followed by its height and
 * width and the names of its video files, the way Bluesky's apps add them so a feed can play the video instead.
 */
export function gifAddress(gif: KlipyFile, mp4: string | undefined, webm: string | undefined): string {
  const params = new URLSearchParams()
  params.set('hh', String(gif.height))
  params.set('ww', String(gif.width))
  const mp4Slug = fileSlug(mp4)
  const webmSlug = fileSlug(webm)
  if (mp4Slug) params.set('mp4', mp4Slug)
  if (webmSlug) params.set('webm', webmSlug)
  return `${gif.url}${gif.url.includes('?') ? '&' : '?'}${params.toString()}`
}

/** Klipy's answer to a search, as the grid shows it. Anything that is not a GIF with an address is left out; the order is Klipy's. */
export function parseKlipyPage(body: unknown): KlipyPage {
  const data = isRecord(body) && isRecord(body.data) ? body.data : {}
  const items = Array.isArray(data.data) ? (data.data as unknown[]) : []
  const gifs: KlipyGif[] = []

  for (const item of items) {
    if (!isRecord(item)) continue
    const post = firstSize(item.file, POST_SIZES)
    // A result with no GIF file is left out: the post is a GIF address, so such a result could not be posted.
    if (!post) continue
    const grid = firstSize(item.file, GRID_SIZES) ?? post
    const sizeFiles = isRecord(post.sizeFiles) ? post.sizeFiles : {}
    gifs.push({
      id: String(item.id ?? post.gif.url),
      title: typeof item.title === 'string' ? item.title.trim() : '',
      previewUrl: grid.gif.url,
      previewWidth: grid.gif.width,
      previewHeight: grid.gif.height,
      url: gifAddress(post.gif, fileAt(sizeFiles, 'mp4')?.url, fileAt(sizeFiles, 'webm')?.url),
    })
  }

  return {
    gifs,
    page: typeof data.current_page === 'number' ? data.current_page : 1,
    hasNext: data.has_next === true,
  }
}

/** The country part of a browser language (`en-US` is `us`), which Klipy takes as the person's locale; empty when there is none. */
export function klipyLocale(language: string | undefined): string {
  const region = /^[a-z]{2,3}[-_]([a-z]{2})\b/i.exec(language ?? '')
  return region ? region[1].toLowerCase() : ''
}

/** The address of one page of a search. */
export function searchAddress(key: string, query: string, page: number, customerId: string, locale: string): string {
  const params = new URLSearchParams()
  params.set('q', query)
  params.set('page', String(page))
  params.set('per_page', String(KLIPY_PER_PAGE))
  params.set('customer_id', customerId)
  if (locale) params.set('locale', locale)
  params.set('content_filter', 'high')
  return `${KLIPY_API}/api/v1/${encodeURIComponent(key)}/gifs/search?${params.toString()}`
}

const CUSTOMER_KEY = 'modbot.klipyCustomer'

/**
 * A random number this browser tells Klipy it is, the same each time, so Klipy can tell one searcher from another.
 * It is not the person's Modbot account, and it stays in this browser.
 */
export function klipyCustomerId(store: Pick<Storage, 'getItem' | 'setItem'> | undefined): string {
  try {
    const kept = store?.getItem(CUSTOMER_KEY)
    if (kept) return kept
    const made = crypto.randomUUID()
    store?.setItem(CUSTOMER_KEY, made)
    return made
  } catch {
    return crypto.randomUUID()
  }
}

/** One page of a search, asked of Klipy by this browser. */
export async function searchKlipy(
  key: string,
  query: string,
  page: number,
  customerId: string,
  locale: string,
  signal?: AbortSignal,
  ask: typeof fetch = fetch,
): Promise<KlipyPage> {
  const response = await ask(searchAddress(key, query, page, customerId, locale), { signal })
  if (!response.ok) throw new Error(`Klipy answered ${response.status}.`)
  return parseKlipyPage(await response.json())
}
