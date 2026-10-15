/** The World menu's three choices on the event form: no world, a world id typed in, or a world list. */
export const NO_WORLD = ''
export const OTHER_WORLD = '__other__'
export const FROM_LIST = '__list__'

/**
 * Which choice the World menu shows. An event that already has a world, but no list, shows it as a
 * World id; the menu has no row for individual worlds.
 */
export function worldChoice(state: { fromList: boolean; typedWorld: boolean; worldId: string | null }): string {
  if (state.fromList) return FROM_LIST
  return state.typedWorld || state.worldId !== null ? OTHER_WORLD : NO_WORLD
}
