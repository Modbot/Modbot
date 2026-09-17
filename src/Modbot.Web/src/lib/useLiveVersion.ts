import { useCallback, useEffect, useRef, useState } from 'react'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveStream } from '@/lib/useLiveStream'

/**
 * A number that goes up when a live event changes what a screen is showing.
 *
 * A screen puts it in the dependencies of its load, or in `useLoad`'s `version`, and reads again
 * when it moves. Events are settled over a short moment first, so a burst -- six moderators
 * reporting one join, a sweep writing forty facts -- is one read, not forty. Only the screen on
 * view asks: the hook lives in the component, and a component that is not mounted asks nothing.
 */
export function useLiveVersion(matches: (event: LiveEvent) => boolean, settleMs = 400): number {
  const [version, setVersion] = useState(0)
  const timer = useRef<number | undefined>(undefined)

  useLiveStream(
    useCallback(
      (event: LiveEvent) => {
        if (!matches(event)) return
        window.clearTimeout(timer.current)
        timer.current = window.setTimeout(() => setVersion((v) => v + 1), settleMs)
      },
      [matches, settleMs],
    ),
  )

  useEffect(() => () => window.clearTimeout(timer.current), [])

  return version
}
