import { Fragment, useCallback, useState } from 'react'
import { TryAgainContext } from '@/lib/tryAgain'

/**
 * A part of the screen that "Try again" draws afresh when a failed row inside it gives no other
 * way (`lib/tryAgain.ts`): the page, and the popup over it.
 *
 * With a `className` it is a `div` (the page's padded box, so the page needs no extra wrapper);
 * without one it adds nothing to the page, for a popup whose content leaves for a portal.
 */
export function TryAgainArea({ className, children }: { className?: string; children: React.ReactNode }) {
  const [round, setRound] = useState(0)
  const again = useCallback(() => setRound((n) => n + 1), [])

  return (
    <TryAgainContext.Provider value={again}>
      {className === undefined ? (
        <Fragment key={round}>{children}</Fragment>
      ) : (
        <div key={round} className={className}>
          {children}
        </div>
      )}
    </TryAgainContext.Provider>
  )
}
