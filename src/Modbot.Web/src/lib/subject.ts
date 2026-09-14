import { useLocation, go } from '@/lib/router'

/**
 * What the popup is open on, and the stack of things opened to get there.
 *
 * Spec 10.2 asks for a *person* over the current view, opened from anywhere a name appears, and
 * says why: moderation is interruption-driven, so looking something up must not cost the scroll
 * position and the filters of the scan it interrupted. That reasoning is unchanged. What changed
 * is that a person is no longer the only thing worth opening — a world and a room are too, and
 * each of them names the others.
 *
 * ## The URL carries the whole stack
 *
 * `?subject=` repeated, in the order things were opened:
 *
 * - `?subject=usr_abc` — one person. **Exactly the link spec 10.2 specified**, so every URL a
 *   moderator has already pasted somewhere still opens the same thing.
 * - `?subject=world:wrld_abc` — one world.
 * - `?subject=usr_abc&subject=instance:6f3e…` — a person, then a room opened from inside it.
 *
 * A repeated parameter rather than one comma-separated value, because each value is
 * percent-encoded on its own: a comma separator would be indistinguishable from a comma inside an
 * id, and VRChat ids are arbitrary text (spec 3.1.1). The prefixes are Modbot's own namespacing
 * on a value Modbot wrote, not an inference about the shape of an id — a person is written bare
 * so the old links keep working, and anything unprefixed is read as a person.
 *
 * ## Opening stacks; closing goes back one
 *
 * A world opened from inside a room popup does not replace it — it is pushed on top, and closing
 * returns to the room underneath. Escape, the close button and the browser's back button all do
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
export type SubjectKind = 'person' | 'world' | 'instance'

export type Subject = { kind: SubjectKind; id: string }

/** The old name, kept: a link written before worlds and rooms existed still says `subject`. */
const PARAM = 'subject'

/** Marks a history entry this module pushed, so closing knows whether back is safe. */
const PUSHED = { modbotSubject: true }

export function encodeSubject(subject: Subject): string {
  return subject.kind === 'person' ? subject.id : `${subject.kind}:${subject.id}`
}

export function decodeSubject(value: string): Subject | null {
  if (!value) return null

  for (const kind of ['world', 'instance', 'person'] as const) {
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
export function openSubject(subject: Subject): void {
  const params = new URLSearchParams(window.location.search)
  const stack = params.getAll(PARAM)

  if (stack.length > 0 && sameSubject(decodeSubject(stack[stack.length - 1]), subject)) return

  params.append(PARAM, encodeSubject(subject))
  go(`${window.location.pathname}?${params.toString()}`, { state: PUSHED })
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
  for (const value of stack.slice(0, -1)) params.append(PARAM, value)

  const query = params.toString()
  go(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
}

/** Closes every popup at once. */
export function closeAllSubjects(): void {
  const params = new URLSearchParams(window.location.search)
  if (params.getAll(PARAM).length === 0) return

  params.delete(PARAM)
  const query = params.toString()
  go(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
}

/** A person, which is what most callers open. Kept as a function so lists can pass it directly. */
export function openPerson(id: string): void {
  openSubject({ kind: 'person', id })
}

export function openWorld(id: string): void {
  openSubject({ kind: 'world', id })
}

export function openInstance(id: string): void {
  openSubject({ kind: 'instance', id })
}
