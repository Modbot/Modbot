/**
 * The part of the page a phone's on-screen keyboard leaves in sight, written into three CSS
 * variables on the root so a sheet can sit above the keyboard and Search can stop short of it:
 *
 * - `--visible-h`: how tall the part in sight is
 * - `--visible-top`: how far down the page it starts
 * - `--keyboard-h`: how much of the bottom of the page the keyboard covers
 *
 * Android's browsers read `interactive-widget=resizes-content` in `index.html` and shrink the page
 * itself above the keyboard, so there the keyboard covers none of it and this writes 0. Safari on
 * iOS ignores that setting: the page keeps its height, only the part in sight (the visual viewport)
 * shrinks, and a sheet pinned to the bottom of the page sat under the keyboard.
 */

export interface VisibleArea {
  height: number
  top: number
  keyboard: number
}

/**
 * @param page How tall the page is (the layout viewport), which the keyboard does not change on iOS.
 * @param seen The visual viewport: how tall the part in sight is, how far down the page it starts,
 *   and how far it is zoomed in.
 */
export function visibleArea(page: number, seen: { height: number; offsetTop: number; scale: number }): VisibleArea {
  // Zoomed in with two fingers, the part in sight is small for a reason that is not a keyboard. A
  // sheet shrunk to match would be a sliver, so a zoomed page is treated as all in sight.
  if (seen.scale > 1.01) return { height: page, top: 0, keyboard: 0 }

  const top = Math.max(0, seen.offsetTop)
  const height = Math.min(page, seen.height)
  return { height, top, keyboard: Math.max(0, page - height - top) }
}

/** Keeps the three variables up to date for as long as the page is open. Called once, at start. */
export function watchVisibleArea(): void {
  const seen = window.visualViewport
  if (!seen) return

  const root = document.documentElement
  // A variable on the root restyles the whole page when it changes, and panning a zoomed page
  // fires `scroll` on every frame with the same answer each time, so an unchanged one is left be.
  let last = ''
  const write = () => {
    const area = visibleArea(root.clientHeight, seen)
    const key = `${area.height} ${area.top} ${area.keyboard}`
    if (key === last) return
    last = key
    root.style.setProperty('--visible-h', `${area.height}px`)
    root.style.setProperty('--visible-top', `${area.top}px`)
    root.style.setProperty('--keyboard-h', `${area.keyboard}px`)
  }

  write()
  seen.addEventListener('resize', write)
  seen.addEventListener('scroll', write)
}
