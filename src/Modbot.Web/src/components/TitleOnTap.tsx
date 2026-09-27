import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

/** Where a tap on these goes is where their detail is: they open what the title only names. */
const OPENS_SOMETHING = 'a, button, summary, label, input, select, textarea, [role="button"], [aria-expanded]'

/**
 * In a headset, a tap on anything carrying a `title` shows that title under it.
 *
 * A title is shown by the browser only while a pointer rests on it, and a laser pointer does not
 * rest (UX review 2026-09-25, finding 16). About forty places put a detail there -- a whole date
 * behind "3h ago", an id behind a name, an instance number behind an instance's name -- so rather
 * than change each of them this listens once, for the whole app. index.css marks the same titles
 * with a dotted line so there is something to aim at.
 *
 * Left alone: a title on a link or a button, whose own tap opens the thing the title names, and a
 * title that says no more than the text it sits on, which index.css writes out in full instead.
 * At a desk this does nothing; the browser's own tooltip is right there.
 */
export function TitleOnTap() {
  const [shown, setShown] = useState<{ text: string; x: number; y: number; target: Element } | null>(null)
  const bubble = useRef<HTMLDivElement>(null)

  // Under the middle of what was tapped, and moved in from the edge of the screen when that would
  // run off it. Its width is only known once it is drawn.
  useLayoutEffect(() => {
    const el = bubble.current
    if (!el || !shown) return
    const width = el.getBoundingClientRect().width
    const left = Math.min(Math.max(8, shown.x - width / 2), window.innerWidth - 8 - width)
    el.style.left = `${Math.max(8, left)}px`
    el.style.visibility = 'visible'
  }, [shown])

  useEffect(() => {
    const onClick = (event: MouseEvent) => {
      if (document.documentElement.dataset.place !== 'headset') return
      const target = event.target instanceof Element ? event.target.closest('[title]') : null
      const text = target?.getAttribute('title')?.trim()

      setShown((now) => {
        if (!target || !text || now?.target === target) return null
        if (target.closest(OPENS_SOMETHING)) return null
        if (text === target.textContent?.trim()) return null
        const box = target.getBoundingClientRect()
        return { text, x: box.left + box.width / 2, y: box.bottom, target }
      })
    }
    const close = () => setShown(null)
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') close()
    }

    document.addEventListener('click', onClick, true)
    document.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', onClick, true)
      document.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
      document.removeEventListener('keydown', onKey)
    }
  }, [])

  if (!shown) return null

  return createPortal(
    <div
      ref={bubble}
      role="tooltip"
      className="pointer-events-none fixed z-[60] w-max max-w-[min(24rem,calc(100vw-1rem))] rounded-sm bg-foreground px-2 py-1 text-balance break-words text-background"
      style={{
        left: 0,
        top: shown.y + 6,
        visibility: 'hidden',
        fontSize: 'var(--text-small)',
      }}
    >
      {shown.text}
    </div>,
    document.body,
  )
}
