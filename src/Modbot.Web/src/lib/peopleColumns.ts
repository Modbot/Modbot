/**
 * Which columns of the People table a moderator has turned off, kept in the browser.
 *
 * Each of the table's three views remembers its own choice: the People page, the member list (the
 * Members and People who left views of it) and an instance popup's People tab. What is stored is
 * the columns turned off, not the ones left on, so a column added later shows until somebody
 * hides it. The person column always shows and is not here.
 */

export type PeopleColumnsKind = 'people' | 'members' | 'instance'

export type PeopleColumn = { id: string; label: string }

const KEY = 'modbot.people.columns'

/**
 * A view's columns in the order the table draws them. The member list's two views differ in their
 * date columns, and Discord is only for those who see profiles.
 */
export function peopleColumns(
  kind: PeopleColumnsKind,
  options: { view?: 'member' | 'left' | null; profiles?: boolean } = {},
): PeopleColumn[] {
  if (kind === 'instance') {
    return [
      { id: 'standing', label: 'Standing' },
      { id: 'arrived', label: 'Arrived at' },
      { id: 'leftAt', label: 'Left at' },
      { id: 'visits', label: 'Visits' },
    ]
  }

  if (kind === 'people') {
    return [
      { id: 'standing', label: 'Standing' },
      { id: 'lastSeen', label: 'Last seen by Modbot' },
      { id: 'firstSeen', label: 'First seen' },
    ]
  }

  return [
    ...(options.profiles ? [{ id: 'discord', label: 'Discord' }] : []),
    { id: 'roles', label: 'Roles' },
    { id: 'joined', label: 'Joined' },
    ...(options.view === 'left' ? [{ id: 'left', label: 'Left' }] : []),
    { id: 'lastSeen', label: 'Last seen by Modbot' },
    ...(options.view === 'member' ? [{ id: 'firstSeen', label: 'First seen' }] : []),
  ]
}

/** The columns turned off for a view: none when nothing is stored, or the store is blocked or garbled. */
export function loadHiddenColumns(kind: PeopleColumnsKind): string[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(`${KEY}.${kind}`) ?? '[]')
    return Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === 'string') : []
  } catch {
    return []
  }
}

export function saveHiddenColumns(kind: PeopleColumnsKind, hidden: readonly string[]): void {
  try {
    localStorage.setItem(`${KEY}.${kind}`, JSON.stringify(hidden))
  } catch {
    // A blocked store forgets; the table still shows what it was told for this visit.
  }
}

/** The columns turned off once `id` is turned on or off. */
export function withColumn(hidden: readonly string[], id: string, on: boolean): string[] {
  const rest = hidden.filter((h) => h !== id)
  return on ? rest : [...rest, id]
}
