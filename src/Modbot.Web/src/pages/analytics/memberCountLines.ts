/**
 * Which of the member count chart's two lines a viewer has chosen to see.
 *
 * Remembered in this browser only: it is how one person likes to read one chart, and nothing else
 * in Modbot changes for it. A store that cannot be read or written (a private window, blocked site
 * data) just means both lines, every time.
 */

export type MemberCountLines = { members: boolean; online: boolean }

export const BOTH_LINES: MemberCountLines = { members: true, online: true }

const KEY = 'modbot.memberCountLines'

/**
 * The choice with one line switched. The last line showing cannot be switched off: a chart of no
 * lines is an empty box that looks broken, so the change is refused and the choice stays as it was.
 */
export function switchLine(lines: MemberCountLines, line: keyof MemberCountLines, on: boolean): MemberCountLines {
  const next = { ...lines, [line]: on }
  return next.members || next.online ? next : lines
}

export function recallLines(): MemberCountLines {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return BOTH_LINES

    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return BOTH_LINES

    const { members, online } = parsed as Record<string, unknown>
    const lines = { members: members !== false, online: online !== false }

    // A stored "neither" is not a choice anybody could have made here; show both.
    return lines.members || lines.online ? lines : BOTH_LINES
  } catch {
    return BOTH_LINES
  }
}

export function rememberLines(lines: MemberCountLines): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(lines))
  } catch {
    // A blocked store forgets; the chart still shows what was picked until the page is left.
  }
}
