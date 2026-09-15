// The popup stack, as the app keeps it: opening from inside a popup puts a new one on top, and
// Back, Escape or a click outside closes one level, never the whole stack.

export type SubjectKind = 'person' | 'world' | 'instance'

export interface Subject {
  kind: SubjectKind
  id: string
}

export type Stack = readonly Subject[]

export const open = (stack: Stack, subject: Subject): Stack => [...stack, subject]

export const closeOne = (stack: Stack): Stack => stack.slice(0, -1)

export const top = (stack: Stack): Subject | undefined => stack[stack.length - 1]

/** "Back to the world", naming the popup underneath; null when there is nothing underneath. */
export function backLabel(stack: Stack): string | null {
  const below = stack.length > 1 ? stack[stack.length - 2] : undefined
  return below ? `Back to the ${below.kind}` : null
}
