import { useRef } from 'react'
import { isPhoneLayout } from '@/lib/phoneLayout'

/**
 * Opens a tab from somewhere above the tab row, and on a phone brings the row up to the top.
 *
 * On a phone the tabs come after the whole left column, so a tab opened from the header menu or a
 * button above would change somewhere the reader cannot see. Put `ref` on what wraps the tabs
 * (`PopupTabs` does). "A phone" is the popup's phone layout, which a phone on its side keeps
 * (`lib/phoneLayout.ts`).
 *
 * `pick` is for the tab row itself, which on a phone stays pinned under the header. Picked while
 * the reader was far down the last tab, the new one would open at that same depth, part way
 * through; it starts at its top instead. Picked from the top of the popup, nothing moves.
 */
export function useOpenFromAbove<T>(setTab: (next: T) => void) {
  const ref = useRef<HTMLDivElement>(null)

  const open = (next: T) => {
    setTab(next)
    if (isPhoneLayout()) requestAnimationFrame(() => ref.current?.scrollIntoView({ block: 'start' }))
  }

  const pick = (next: T) => {
    setTab(next)
    const at = ref.current
    const row = at?.querySelector('[role="tablist"]')
    if (at && row && isPhoneLayout() && row.getBoundingClientRect().top > at.getBoundingClientRect().top + 1)
      requestAnimationFrame(() => at.scrollIntoView({ block: 'start' }))
  }

  return [ref, open, pick] as const
}
