import { useEffect, useRef, useSyncExternalStore } from 'react'

/**
 * The keyboard: one registry, one listener on the window.
 *
 * Modelled on Linear's keys (research 2026-09-16): `Ctrl/Cmd+K` for the command palette, `?` for
 * the shortcut sheet, `g` then a letter to go to a page, `j`/`k` to move in a list, `Enter` to
 * open, `Esc` to close, `f` for the filter bar, `/` for search.
 *
 * Two rules decide everything else here:
 *
 * - **A key typed into a field is text, never a shortcut.** Only keys with Ctrl or Cmd held, and
 *   Escape, fire while an input, a textarea, a select or an editable element has focus. So `j`
 *   in the search box is a letter and `Ctrl+K` in the search box still opens the palette.
 * - **A page's keys go quiet while a popup is open.** `j` and `Enter` belong to the list behind
 *   the popup, and moving that list's selection under a dialog nobody can see would be a surprise.
 *   Dialogs say they are open with `useModal`, and shortcuts marked `page` wait.
 *
 * Every page registers its own keys and unregisters them when it unmounts, so the sheet lists
 * exactly what works on the screen that is open.
 *
 * The same registry is the page's list of actions for the Actions sheet on a phone or in a
 * headset, so an action can be registered without a key: Refresh, Clear filters, a sort order.
 * The other way round, a key that only means something at a keyboard, such as moving a row
 * selection, is marked `keyboardOnly` and left out of that sheet (review 2026-09-27, idea 8).
 */

export type ShortcutGroup = 'General' | 'Go to' | 'Page' | 'Lists' | 'Filters' | 'Sort' | 'Calendar' | 'Popups'

export type Shortcut = {
  /**
   * The keys, lower case, modifiers first: `mod+k`, `?`, `j`, `escape`, `enter`, or a chord
   * such as `g m` (press `g`, then `m` within a second). `mod` is Ctrl on Windows and Linux and
   * Cmd on a Mac. Left out for an action with no key, which only the Actions sheet and the command
   * palette offer.
   */
  keys?: string
  /** What it does, for the sheet and the palette. */
  label: string
  group: ShortcutGroup
  /** Belongs to the page behind any popup: does not fire while one is open. */
  page?: boolean
  /** Left out of the sheet and the palette: a key that only makes sense with the mouse on a row. */
  hidden?: boolean
  /**
   * Left out of the Actions sheet on a phone or in a headset: a key that only means something at
   * a keyboard, such as moving the row selection, or one that stands for a control already in
   * reach there, such as the page's search box.
   */
  keyboardOnly?: boolean
  /** One of a set of choices, such as a sort order, and the one in use. The sheet marks it. */
  checked?: boolean
  run: (event: KeyboardEvent) => void
}

/** How long the first key of a chord waits for the second. */
export const CHORD_TIMEOUT_MS = 1000

// ── The registry ────────────────────────────────────────────────────────────────────────────

let registered: Shortcut[] = []
const listeners = new Set<() => void>()

function notify() {
  for (const l of listeners) l()
}

/** Adds one shortcut; the function returned takes it away again. Later registrations win. */
export function registerShortcut(shortcut: Shortcut): () => void {
  registered = [...registered, shortcut]
  notify()

  return () => {
    registered = registered.filter((s) => s !== shortcut)
    notify()
  }
}

/** Everything registered right now, in registration order. */
export function listShortcuts(): Shortcut[] {
  return registered
}

/** The registered shortcuts as React state, so the sheet redraws when a page changes them. */
export function useShortcutList(): Shortcut[] {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb)
      return () => listeners.delete(cb)
    },
    listShortcuts,
    listShortcuts,
  )
}

/**
 * Registers shortcuts for as long as the component is mounted.
 *
 * The list is rebuilt on every render and need not be memoised: what is registered changes only
 * when the keys, labels or groups do, and a key press always runs the handler from the latest
 * render, so a handler may close over state freely.
 */
export function useShortcuts(shortcuts: Shortcut[]): void {
  const latest = useRef(shortcuts)

  useEffect(() => {
    latest.current = shortcuts
  })

  const signature = shortcuts
    .map((s) =>
      [s.keys ?? '', s.label, s.group, s.page ? 1 : 0, s.hidden ? 1 : 0, s.keyboardOnly ? 1 : 0, s.checked ? 1 : 0].join('\t'),
    )
    .join('\n')

  useEffect(() => {
    const away = latest.current.map((s, i) =>
      registerShortcut({ ...s, run: (event) => latest.current[i]?.run(event) }),
    )
    return () => away.forEach((off) => off())
  }, [signature])
}

// ── Popups and dialogs ──────────────────────────────────────────────────────────────────────

let modalDepth = 0

/** Says a dialog is open for as long as the component is mounted, so page keys wait. */
export function useModal(): void {
  useEffect(() => {
    modalDepth++
    return () => {
      modalDepth--
    }
  }, [])
}

/**
 * Whether anything modal is open: a popup or dialog that said so, or any Radix dialog or popover
 * on the page, which carry `role="dialog"` while open. The second check catches the confirmation
 * dialogs and pickers that never call `useModal`, so `Enter` on a confirm button is never also
 * `Enter` on the list behind it.
 */
export function isModalOpen(): boolean {
  if (modalDepth > 0) return true
  return typeof document !== 'undefined' && document.querySelector('[role="dialog"][data-state="open"]') !== null
}

// ── Pure parts, kept free of the DOM so they can be tested with Node alone ──────────────────

/**
 * The groups that are the app's rather than the page's: the palette and this sheet, and a key for
 * every page. On a phone the bar at the foot already has Search and Menu for them, and nobody
 * holding one wants a list of keys.
 */
export const APP_GROUPS: readonly ShortcutGroup[] = ['General', 'Go to']

/**
 * What the Actions sheet on a phone or in a headset offers: the page's own actions, without the
 * app's (the bar has Menu and Search for those) and without the keys that only mean something at a
 * keyboard. Row moves were all it held on a list page, and with a finger or a laser pointer you
 * point at the row (review 2026-09-27, idea 8).
 */
export function isPageAction(shortcut: Pick<Shortcut, 'group' | 'hidden' | 'keyboardOnly'>): boolean {
  return !shortcut.hidden && !shortcut.keyboardOnly && !APP_GROUPS.includes(shortcut.group)
}

/**
 * Whether the screen that is open has anything of its own to do. The bar at the foot of a phone
 * shows Actions only when it does (review 2026-09-27, finding 8).
 */
export function hasPageActions(shortcuts: readonly Pick<Shortcut, 'group' | 'hidden' | 'keyboardOnly'>[]): boolean {
  return shortcuts.some(isPageAction)
}

/** What the `?` sheet lists: every key, and nothing that has no key. */
export function isListedKey(shortcut: Pick<Shortcut, 'keys' | 'hidden'>): boolean {
  return !shortcut.hidden && shortcut.keys !== undefined
}

/** Whether typing into this element is what a key press means. */
export function isTyping(target: {
  tagName?: string
  type?: string
  isContentEditable?: boolean
} | null): boolean {
  if (!target) return false
  if (target.isContentEditable) return true

  const tag = target.tagName?.toUpperCase()
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true
  if (tag !== 'INPUT') return false

  // A checkbox, a radio or a button is an input in name only.
  const type = (target.type ?? 'text').toLowerCase()
  return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'file', 'color'].includes(type)
}

/**
 * One key press as a token: `mod+k`, `?`, `j`, `escape`, `arrowdown`.
 *
 * Shift is not written for printable keys — `?` is what the key produced, and `shift+/` is what
 * it took to produce it, which differs between keyboards. Shift is kept for named keys such as
 * `shift+enter`, where there is no character to speak for it.
 */
export function keyToken(event: {
  key: string
  ctrlKey?: boolean
  metaKey?: boolean
  altKey?: boolean
  shiftKey?: boolean
}): string | null {
  const key = event.key
  if (!key || key === 'Control' || key === 'Meta' || key === 'Alt' || key === 'Shift') return null

  const printable = key.length === 1
  const parts: string[] = []
  if (event.ctrlKey || event.metaKey) parts.push('mod')
  if (event.altKey) parts.push('alt')
  if (event.shiftKey && !printable) parts.push('shift')
  parts.push(key.toLowerCase())

  return parts.join('+')
}

/**
 * Decides what a token does against a list of key strings, given the chord key pressed a
 * moment ago (if any).
 *
 * Returns the matched keys, or the token to hold as the start of a chord, or nothing.
 */
export function matchKeys(
  keys: readonly string[],
  pending: string | null,
  token: string,
): { run?: string; pending?: string } {
  if (pending) {
    const chord = `${pending} ${token}`
    if (keys.includes(chord)) return { run: chord }
    // A chord that went nowhere falls through to the single key, so `g` then `j` still moves
    // the list rather than being swallowed.
  }

  if (keys.some((k) => k.startsWith(`${token} `))) return { pending: token }
  if (keys.includes(token)) return { run: token }

  return {}
}

/**
 * Each key of a chord as a person reads it: `g m` is `['G', 'M']`, `mod+k` is `['Ctrl K']`. Every
 * key hint on screen is spelled from this, so a key never reads `g` in one place and `G` in another:
 * `Kbd` draws one box per name, and the sidebar's go-to chords are the names side by side.
 */
export function keyNames(keys: string, mac: boolean): string[] {
  const names: Record<string, string> = {
    mod: mac ? '⌘' : 'Ctrl',
    alt: mac ? '⌥' : 'Alt',
    shift: 'Shift',
    escape: 'Esc',
    enter: 'Enter',
    arrowdown: '↓',
    arrowup: '↑',
    arrowleft: '←',
    arrowright: '→',
    ' ': 'Space',
  }

  return keys.split(' ').map((combo) =>
    combo
      .split('+')
      .map((part) => names[part] ?? (part.length === 1 ? part.toUpperCase() : part))
      .join(' '),
  )
}

/** The keys as a person reads them: `Ctrl K`, `⌘ K`, `G then M`, `?`. */
export function describeKeys(keys: string, mac: boolean): string {
  return keyNames(keys, mac).join(' then ')
}

// ── The listener ────────────────────────────────────────────────────────────────────────────

let pendingChord: string | null = null
let chordTimer: ReturnType<typeof setTimeout> | null = null
const chordListeners = new Set<() => void>()

function holdChord(chord: string | null) {
  if (chord === pendingChord) return
  pendingChord = chord
  for (const l of chordListeners) l()
}

/** The first key of a chord still waiting for its second, such as `g`, or null. */
export function waitingChord(): string | null {
  return pendingChord
}

/**
 * The waiting chord key as React state. The sidebar shows every go-to key while `g` waits, and for
 * exactly as long: a key still on screen after the second press stopped working would be wrong.
 */
export function useWaitingChord(): string | null {
  return useSyncExternalStore(
    (cb) => {
      chordListeners.add(cb)
      return () => chordListeners.delete(cb)
    },
    waitingChord,
    waitingChord,
  )
}

/** The one keydown listener. Exported for the tests, which press keys without a browser. */
export function onKeyDown(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey' | 'target' | 'defaultPrevented' | 'isComposing' | 'preventDefault'>) {
  if (event.defaultPrevented || event.isComposing) return

  const token = keyToken(event)
  if (!token) return

  const typing = isTyping(event.target as HTMLElement | null)
  const modal = isModalOpen()

  const candidates = registered.filter((s): s is Shortcut & { keys: string } => {
    if (s.keys === undefined) return false
    if (typing && !s.keys.includes('mod+') && s.keys !== 'escape') return false
    if (modal && s.page) return false
    return true
  })

  const outcome = matchKeys(
    candidates.map((s) => s.keys),
    pendingChord,
    token,
  )

  if (chordTimer) {
    clearTimeout(chordTimer)
    chordTimer = null
  }
  holdChord(outcome.pending ?? null)

  if (outcome.pending) {
    chordTimer = setTimeout(() => {
      chordTimer = null
      holdChord(null)
    }, CHORD_TIMEOUT_MS)
    event.preventDefault()
    return
  }

  if (!outcome.run) return

  // The last one registered wins, so a page can shadow a global key while it is open.
  const shortcut = [...candidates].reverse().find((s) => s.keys === outcome.run)
  if (!shortcut) return

  event.preventDefault()
  shortcut.run(event as KeyboardEvent)
}

let installed = false

/** Attaches the one keydown listener. Safe to call more than once. */
export function useKeyboard(): void {
  useEffect(() => {
    if (installed) return
    installed = true
    window.addEventListener('keydown', onKeyDown)

    return () => {
      window.removeEventListener('keydown', onKeyDown)
      installed = false
    }
  }, [])
}

export const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
