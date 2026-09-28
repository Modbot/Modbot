import { useRef } from 'react'
import { isPhoneLayout } from '@/lib/phoneLayout'

/**
 * Opens a tab from somewhere above the tab row, and on a phone brings the row up to the top.
 *
 * On a phone the tabs come after the whole left column, so a tab opened from the header menu or a
 * button above would change somewhere the reader cannot see. Put `ref` on what wraps the tabs.
 * "A phone" is the popup's phone layout, which a phone on its side keeps (`lib/phoneLayout.ts`).
 */
export function useOpenFromAbove<T>(setTab: (next: T) => void) {
  const ref = useRef<HTMLDivElement>(null)
  const open = (next: T) => {
    setTab(next)
    if (isPhoneLayout()) requestAnimationFrame(() => ref.current?.scrollIntoView({ block: 'start' }))
  }
  return [ref, open] as const
}
