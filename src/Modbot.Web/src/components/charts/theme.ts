/**
 * Chart colours and sizes, all read from the CSS tokens in index.css.
 *
 * Nothing here is a hex value. The five series colours are a fixed, validated order (see the
 * note on `--series-*` in index.css): a series keeps its hue when a filter removes its
 * neighbours, so "bans are orange" stays learnable. Callers pick a slot by number, never by
 * rank, and the theme swaps the actual colour underneath for light, dark and VR.
 *
 * Shared by every chart in the app -- analytics, storage, anything later -- so that a change to
 * the palette is a change to index.css and nothing else.
 */

export type SeriesSlot = 1 | 2 | 3 | 4 | 5

export const seriesColor = (slot: SeriesSlot): string => `var(--series-${slot})`

/** The slot after `slot`, wrapping, for callers that lay out an unknown number of series. */
export const nextSlot = (index: number): SeriesSlot => (((index % 5) + 5) % 5 + 1) as SeriesSlot

export const chartTheme = {
  grid: 'var(--chart-grid)',
  text: 'var(--muted-foreground)',
  surface: 'var(--card)',
  strokeWidth: 'var(--chart-stroke)',
  /** The status colours, for marks that mean something rather than identify a series. */
  ok: 'var(--ok)',
  warn: 'var(--warn)',
  bad: 'var(--destructive)',
} as const

/** Default plot heights, in pixels. Small enough that four fit on a screen; tall enough to read. */
export const chartHeight = {
  small: 104,
  regular: 160,
  tall: 220,
} as const
