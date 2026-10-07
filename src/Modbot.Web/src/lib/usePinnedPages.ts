import { useRef, useState } from 'react'
import { api, type CurrentUser } from '@/lib/api'
import { withPinToggled, type PageId } from '@/lib/nav'

/**
 * The pages this person pinned in the menu, and the tap that pins or unpins one.
 *
 * Kept on the account (`PUT /api/auth/pinned-pages`) so the pins follow the person from their phone
 * to their desk. The list changes on the screen at once and is saved behind it; a save that fails
 * puts the list back as it was. What `me` carries wins when it changes, so a pin made on another
 * device shows up on the next refresh.
 *
 * `pins` is null until the person has pinned or unpinned something, when the defaults show
 * (`pinList` in lib/nav.ts).
 */
export function usePinnedPages(me: CurrentUser): { pins: string[] | null; toggle: (id: PageId) => void } {
  const fromServer = me.pinnedPages ?? null
  const key = JSON.stringify(fromServer)

  const [pins, setPins] = useState<string[] | null>(fromServer)
  const [seen, setSeen] = useState(key)
  const latest = useRef(0)

  // Adjusting state while rendering, the way React documents for a value that follows a prop.
  if (seen !== key) {
    setSeen(key)
    setPins(fromServer)
  }

  const toggle = (id: PageId) => {
    const before = pins
    const next = withPinToggled(before, id)
    const mine = ++latest.current

    setPins(next)
    api
      .setPinnedPages(next)
      .then((saved) => {
        if (latest.current === mine) setPins(saved.pinnedPages ?? null)
      })
      .catch(() => {
        if (latest.current === mine) setPins(before)
      })
  }

  return { pins, toggle }
}
