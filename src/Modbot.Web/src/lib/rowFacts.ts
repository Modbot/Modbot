/**
 * The facts a two-line row writes on its second line, with the ones a row does not have left out.
 *
 * A page lists every column it could show (`person.plainName`, `w.minutesOpen > 0 && ...`) and
 * lets this drop the empty ones, so the line never reads "· · seen 2 h ago" for a person with no
 * plain name. Zero is a fact ("0 visitors") and stays.
 */
export function keptFacts<T>(facts: readonly (T | null | undefined | false | '')[]): T[] {
  return facts.filter((fact): fact is T => fact !== null && fact !== undefined && fact !== false && fact !== '')
}

/**
 * A ban's case files, as a fact on its row: how many there are, or nothing when none is open. A
 * withdrawn one alone counts as none, as the table's column does, since it has nothing to open.
 */
export function caseFileFact(lookup: { caseId: string | null; count: number } | undefined): string | null {
  if (!lookup?.caseId) return null
  return lookup.count === 1 ? '1 case file' : `${lookup.count} case files`
}

/**
 * One moderator's count of each kind of action, in the order the table has its columns, for the
 * list a phone row opens. A kind they never did is 0, so every moderator's list is the same length
 * and reads down the same way.
 */
export function countsByKind(
  kinds: readonly { metric: string; label: string }[],
  byKind: Readonly<Record<string, number>>,
): { label: string; count: number }[] {
  return kinds.map((k) => ({ label: k.label, count: byKind[k.metric] ?? 0 }))
}
