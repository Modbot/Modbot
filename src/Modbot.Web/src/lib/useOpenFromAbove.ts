import { useRef } from 'react'

/**
 * Opens a tab from somewhere above the tab row, and on a phone brings the row up to the top.
 *
 * On a phone the tabs come after the whole left column, so a tab opened from the header menu or a
 * button above would change somewhere the reader cannot see. Put `ref` on what wraps the tabs.
 */
export function useOpenFromAbove<T>(setTab: (next: T) => void) {
  const ref = useRef<HTMLDivElement>(null)
  const open = (next: T) => {
    setTab(next)
    if (window.matchMedia('(max-width: 767px)').matches)
      requestAnimationFrame(() => ref.current?.scrollIntoView({ block: 'start' }))
  }
  return [ref, open] as const
}
