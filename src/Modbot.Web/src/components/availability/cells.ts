/** One hour of a week: `day` is 0 for Monday to 6 for Sunday, `hour` is 0 to 23. */
export type GridCell = { day: number; hour: number }

/** The cell under a point on the page, if it is one of a week grid's. */
export function cellAt(at: { x: number; y: number }): GridCell | null {
  const hit = document.elementFromPoint(at.x, at.y)?.closest<HTMLElement>('[data-week-cell]')
  if (!hit) return null
  return { day: Number(hit.dataset.day), hour: Number(hit.dataset.hour) }
}
