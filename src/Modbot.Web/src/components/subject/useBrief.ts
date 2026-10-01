import { useRef, useState } from 'react'
import { ApiError, type Brief } from '@/lib/api'

/** The reader's own time zone, so the brief's times read the way the rest of the app shows them. */
function timeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

/** One brief being asked for and shown: whether its dialog is open, and what came back. */
export type BriefState = {
  open: boolean
  setOpen: (open: boolean) => void
  brief: Brief | null
  problem: string | null
  /** Opens the dialog and asks. Call it from a click, never from an effect. */
  start: () => void
}

/**
 * Asks for an AI brief and holds the answer (AI chat design §14).
 *
 * **One press, one call.** The request starts in `start`, from the click, never in an effect: a
 * brief is a paid call to the AI provider, and an effect runs twice in development and again on a
 * remount. A press while an earlier one is still out shows only the later answer.
 *
 * Kept apart from the button so a popup can hold it above a tab that remounts on every live update:
 * the person popup's Activity tab does, and the brief's own lookup entry is one such update.
 */
export function useBrief(ask: ((timeZone: string) => Promise<Brief>) | null): BriefState {
  const [open, setOpen] = useState(false)
  const [brief, setBrief] = useState<Brief | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const asked = useRef(0)

  const start = () => {
    if (!ask) return

    const mine = ++asked.current
    setOpen(true)
    setBrief(null)
    setProblem(null)

    ask(timeZone())
      .then((answer) => {
        if (asked.current === mine) setBrief(answer)
      })
      .catch((e: unknown) => {
        if (asked.current !== mine) return
        setProblem(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to ask for a brief.'
            : e instanceof ApiError
              ? e.message
              : 'Could not reach the Modbot server.',
        )
      })
  }

  return { open, setOpen, brief, problem, start }
}
