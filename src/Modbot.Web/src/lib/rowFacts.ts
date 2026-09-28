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
