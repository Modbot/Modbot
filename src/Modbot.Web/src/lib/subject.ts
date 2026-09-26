import { useState } from 'react'
// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node
// test runner resolves neither the alias nor an extensionless path, and the vocabulary this
// module defines -- which kinds exist, how a link encodes one, what an old link still means --
// is worth a test. `router.ts` imports nothing but React, so it loads as it is too.
import { useLocation, go } from './router.ts'

/**
 * What the popup is open on, and the stack of things opened to get there.
 *
 * Spec 10.2 asks for a *person* over the current view, opened from anywhere a name appears, and
 * says why: moderation is interruption-driven, so looking something up must not cost the scroll
 * position and the filters of the scan it interrupted. That reasoning is unchanged. What changed
 * is that a person is no longer the only thing worth opening — a world and an instance are too, and
 * each of them names the others.
 *
 * ## The URL carries the whole stack
 *
 * `?subject=` repeated, in the order things were opened:
 *
 * - `?subject=usr_abc` — one person. **Exactly the link spec 10.2 specified**, so every URL a
 *   moderator has already pasted somewhere still opens the same thing.
 * - `?subject=world:wrld_abc` — one world.
 * - `?subject=usr_abc&subject=instance:6f3e…` — a person, then an instance opened from inside it.
 *
 * ## Three of the kinds are one view
 *
 * `person`, `discord-person` and `account` are three ways of naming the same human being — their
 * VRChat account, their Discord account, and the account they sign in to Modbot with. All three
 * open the **same popup**, which ties the accounts together from whichever one the link named
 * (one view per person design). They stay three values rather than one because a link has to
 * carry which kind of id it holds: the ids are opaque and their shapes say nothing (spec 3.1.1).
 *
 * A repeated parameter rather than one comma-separated value, because each value is
 * percent-encoded on its own: a comma separator would be indistinguishable from a comma inside an
 * id, and VRChat ids are arbitrary text (spec 3.1.1). The prefixes are Modbot's own namespacing
 * on a value Modbot wrote, not an inference about the shape of an id — a person is written bare
 * so the old links keep working, and anything unprefixed is read as a person.
 *
 * ## Opening stacks; closing goes back one
 *
 * A world opened from inside an instance popup does not replace it — it is pushed on top, and closing
 * returns to the instance underneath. Escape, the close button and the browser's back button all do
 * the same thing, because they are the same thing: opening pushes a history entry, so back
 * already pops one level and closing is `history.back()` when Modbot is the one that pushed.
 *
 * Only the top of the stack is on screen. A second dimmed overlay under the first is unreadable
 * and makes Escape ambiguous, so the levels underneath appear as a "Back to …" control in the
 * header instead. That control and the browser's back button do the same thing too.
 *
 * A stack somebody was sent as a link starts with no history behind it, so closing there rewrites
 * the URL rather than calling back — otherwise the first Escape would take them out of Modbot
 * altogether.
 */
export type SubjectKind = 'person' | 'world' | 'instance' | 'discord-person' | 'account'

export type Subject = { kind: SubjectKind; id: string }

/** The old name, kept: a link written before worlds and instances existed still says `subject`. */
const PARAM = 'subject'

/**
 * The one Discord message a popup opened at, when it was opened from a source chip in Chat.
 *
 * Beside the stack rather than inside the subject value, because only the top popup is on screen:
 * there is never more than one message being opened at.
 */
export const MESSAGE = 'message'

/**
 * The tab the top popup opens on, and the profile version it opens at. Beside the stack, like
 * the message, because only the top popup is on screen.
 */
export const TAB = 'tab'
export const VERSION = 'version'

/** Marks a history entry this module pushed, so closing knows whether back is safe. */
const PUSHED = { modbotSubject: true }

export function encodeSubject(subject: Subject): string {
  return subject.kind === 'person' ? subject.id : `${subject.kind}:${subject.id}`
}

export function decodeSubject(value: string): Subject | null {
  if (!value) return null

  for (const kind of ['world', 'instance', 'discord-person', 'account', 'person'] as const) {
    const prefix = `${kind}:`
    if (value.startsWith(prefix) && value.length > prefix.length)
      return { kind, id: value.slice(prefix.length) }
  }

  // Unprefixed is a person, which is what every link written before this scheme existed says.
  return { kind: 'person', id: value }
}

export function sameSubject(a: Subject | null, b: Subject | null): boolean {
  return a !== null && b !== null && a.kind === b.kind && a.id === b.id
}

/** The whole stack, bottom first. Empty when no popup is open. */
export function useSubjects(): Subject[] {
  const [location] = useLocation()

  return location.search
    .getAll(PARAM)
    .map(decodeSubject)
    .filter((s): s is Subject => s !== null)
}

/**
 * Opens a popup on top of whatever is already open.
 *
 * Re-opening the thing already on top does nothing, so a list where the same world appears twice
 * does not build a stack of identical popups.
 */
export function openSubject(subject: Subject, at?: { tab?: string; version?: number }): void {
  const params = new URLSearchParams(window.location.search)
  const stack = params.getAll(PARAM)

  if (stack.length > 0 && sameSubject(decodeSubject(stack[stack.length - 1]), subject) && !at) return

  if (stack.length === 0 || !sameSubject(decodeSubject(stack[stack.length - 1]), subject))
    params.append(PARAM, encodeSubject(subject))

  // The tab and the version belong to the popup being opened, never to the one underneath.
  params.delete(TAB)
  params.delete(VERSION)
  if (at?.tab) params.set(TAB, at.tab)
  if (at?.version !== undefined) params.set(VERSION, String(at.version))

  go(`${window.location.pathname}?${params.toString()}`, { state: PUSHED })
}

/**
 * A Discord account, opened at one of their messages: the Messages tab, on the page that holds it.
 */
export function openDiscordMessage(discordUserId: string, messageId: string): void {
  const params = new URLSearchParams(window.location.search)
  const stack = params.getAll(PARAM)
  const subject: Subject = { kind: 'discord-person', id: discordUserId }

  if (stack.length === 0 || !sameSubject(decodeSubject(stack[stack.length - 1]), subject))
    params.append(PARAM, encodeSubject(subject))

  params.set(MESSAGE, messageId)
  go(`${window.location.pathname}?${params.toString()}`, { state: PUSHED })
}

/** The message the popup on top was opened at, when it was opened at one. */
export function useMessageAt(): string | null {
  const [location] = useLocation()
  return location.search.get(MESSAGE)
}

/**
 * The tab a popup opens on, from the address (`?tab=`), so a link can open a person on their
 * History and the audit log can send a moderator to one version (`?version=`).
 *
 * Read from the address but held in state once the popup is open: switching tabs by hand should
 * not rewrite a link somebody is about to copy into something that opens on the wrong tab.
 */
export function useOpeningTab<T extends string>(fallback: T, allowed: readonly T[]): [T, (next: T) => void] {
  const [location] = useLocation()
  const asked = location.search.get(TAB)
  const [tab, setTab] = useState<T>(() => (allowed.includes(asked as T) ? (asked as T) : fallback))
  return [tab, setTab]
}

/** The version the popup was opened at, when it was opened at one (`?version=<fact id>`). */
export function useOpeningVersion(): number | null {
  const [location] = useLocation()
  const value = location.search.get(VERSION)
  const parsed = value ? Number(value) : NaN
  return Number.isFinite(parsed) ? parsed : null
}

/** Closes the top popup, returning to the one underneath. */
export function closeSubject(): void {
  // Modbot pushed this entry, so the browser's own back is the honest way to leave it: it keeps
  // back and forward meaning what they look like they mean.
  if ((window.history.state as { modbotSubject?: boolean } | null)?.modbotSubject) {
    window.history.back()
    return
  }

  // Nothing of ours behind this entry -- somebody was sent the link. Rewrite rather than go back,
  // or the first Escape would take them out of Modbot.
  const params = new URLSearchParams(window.location.search)
  const stack = params.getAll(PARAM)

  params.delete(PARAM)
  params.delete(MESSAGE)
  params.delete(TAB)
  params.delete(VERSION)
  for (const value of stack.slice(0, -1)) params.append(PARAM, value)

  const query = params.toString()
  go(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
}

/** Closes every popup at once. */
export function closeAllSubjects(): void {
  const params = new URLSearchParams(window.location.search)
  if (params.getAll(PARAM).length === 0) return

  params.delete(PARAM)
  params.delete(MESSAGE)
  params.delete(TAB)
  params.delete(VERSION)
  const query = params.toString()
  go(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
}

/** A person, which is what most callers open. Kept as a function so lists can pass it directly. */
export function openPerson(id: string): void {
  openSubject({ kind: 'person', id })
}

/** A person, on their Profile changes tab, at the version one fact recorded. */
export function openPersonVersion(id: string, factId: number): void {
  openSubject({ kind: 'person', id }, { tab: 'history', version: factId })
}

export function openWorld(id: string): void {
  openSubject({ kind: 'world', id })
}

export function openInstance(id: string): void {
  openSubject({ kind: 'instance', id })
}

/**
 * A Discord account. Its own kind because a link has to say which kind of id it carries, not
 * because it opens something else: it opens the person popup, which ties the accounts together.
 */
export function openDiscordPerson(id: string): void {
  openSubject({ kind: 'discord-person', id })
}

/** A Modbot account — the thing somebody signs in with. Opens the person popup too. */
export function openAccount(id: string): void {
  openSubject({ kind: 'account', id })
}

/** Whether this kind of subject is one of the three ways of naming a human being. */
export function isPerson(subject: Subject): boolean {
  return subject.kind === 'person' || subject.kind === 'discord-person' || subject.kind === 'account'
}
