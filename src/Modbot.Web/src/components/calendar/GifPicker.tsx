import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { Popover } from 'radix-ui'
import { PopoverPortal } from '@/components/ui/dialog-portal'
import {
  KLIPY_PLACEHOLDER,
  klipyCustomerId,
  klipyLocale,
  searchKlipy,
  type KlipyGif,
} from '@/lib/klipy'

/** How long typing pauses before the search is asked. */
const TYPING_PAUSE_MS = 400

/**
 * A button that opens a search of Klipy's GIFs; the one clicked goes to `onPick`. The trigger is `children`.
 * This browser asks Klipy and loads the GIFs itself (Klipy's rule), and the results stay in Klipy's order.
 * A search Klipy refuses is not tried again until the words change.
 */
export function GifPicker({
  gifKey,
  onPick,
  children,
}: {
  gifKey: string
  onPick: (gif: KlipyGif) => void
  children: ReactNode
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [gifs, setGifs] = useState<KlipyGif[]>([])
  const [page, setPage] = useState(1)
  const [hasNext, setHasNext] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)

  async function ask(words: string, wanted: number) {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setLoading(true)
    setError(null)
    try {
      const customer = klipyCustomerId(typeof localStorage === 'undefined' ? undefined : localStorage)
      const result = await searchKlipy(gifKey, words, wanted, customer, klipyLocale(navigator.language), controller.signal)
      setGifs((shown) => (wanted === 1 ? result.gifs : [...shown, ...result.gifs]))
      setPage(result.page)
      setHasNext(result.hasNext)
    } catch (e) {
      if (controller.signal.aborted) return
      setError(e instanceof Error ? e.message : 'Klipy did not answer.')
    } finally {
      if (request.current === controller) setLoading(false)
    }
  }

  // Asks after a pause in typing. An empty box asks nothing and shows nothing.
  useEffect(() => {
    const words = query.trim()
    if (words === '') {
      request.current?.abort()
      setGifs([])
      setHasNext(false)
      setLoading(false)
      setError(null)
      return
    }
    const timer = setTimeout(() => void ask(words, 1), TYPING_PAUSE_MS)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query, gifKey])

  useEffect(() => () => request.current?.abort(), [])

  return (
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        if (!next) setQuery('')
      }}
    >
      <Popover.Trigger asChild>{children}</Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="start"
          sideOffset={4}
          className="z-50 flex w-[340px] flex-col gap-2 rounded-sm border border-(length:--hairline) bg-popover p-2 shadow-sm"
        >
          <input
            type="search"
            aria-label={KLIPY_PLACEHOLDER}
            placeholder={KLIPY_PLACEHOLDER}
            value={query}
            autoFocus
            onChange={(e) => setQuery(e.target.value)}
            className="w-full rounded-sm border border-(length:--hairline) border-input bg-transparent px-2 py-1.5 text-sm outline-none focus:border-ring"
          />
          <div className="grid max-h-[340px] grid-cols-2 gap-1 overflow-y-auto">
            {gifs.map((gif) => (
              <button
                key={gif.id}
                type="button"
                title={gif.title}
                onClick={() => {
                  setOpen(false)
                  setQuery('')
                  onPick(gif)
                }}
                className="overflow-hidden rounded-sm bg-muted focus-visible:outline-2"
              >
                <img src={gif.previewUrl} alt={gif.title} loading="lazy" className="block h-28 w-full object-cover" />
              </button>
            ))}
          </div>
          {loading && <Loader2 className="mx-auto size-4 animate-spin" aria-label="Searching" />}
          {error && (
            <span role="alert" className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {error}
            </span>
          )}
          {hasNext && !loading && (
            <button
              type="button"
              onClick={() => void ask(query.trim(), page + 1)}
              className="rounded-sm border border-(length:--hairline) border-input px-2 py-1 text-sm"
            >
              More
            </button>
          )}
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}
