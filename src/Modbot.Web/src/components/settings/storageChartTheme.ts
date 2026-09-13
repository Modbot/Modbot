import { useEffect, useState } from 'react'

/**
 * Recharts on Modbot's tokens.
 *
 * Every colour, weight and size a chart draws with comes from index.css, so charts move with the
 * theme and with all three densities — VR included, where a 2px line is a suggestion and 11px
 * axis text is mush. Recharts takes CSS `var()` strings happily for colours, but SVG
 * presentation attributes such as `stroke-width` and `font-size` do not resolve `var()`, so
 * those few values are read from the computed style instead and re-read whenever the theme or
 * density class on the root element changes.
 *
 * The rules the shapes follow, because they are easy to undo by accident:
 *
 * - Series colours are a fixed order, never cycled by rank. A filter that removes a series must
 *   not repaint the survivors, or a reader who learned "bans are orange" is misled.
 * - Marks are thin, grid lines are hairline and recessive. Dashed strokes carry exactly one
 *   meaning — an estimate, not a measurement — and measured series are never dashed.
 * - Identity never rides on colour alone: every multi-series chart has a legend, and values are
 *   written in text tokens rather than in the series colour.
 * - One axis, always. Two measures of different scale are two charts.
 */

export type SeriesIndex = 1 | 2 | 3 | 4 | 5

/** The fill or stroke for a series, as a CSS var recharts passes straight through. */
export const seriesColor = (series: SeriesIndex): string => `var(--series-${series})`

export const GRID_COLOR = 'var(--chart-grid)'
export const MUTED_TEXT = 'var(--muted-foreground)'
export const TEXT = 'var(--foreground)'
/** The card surface, for the ring around a dot that sits on a line. */
export const SURFACE = 'var(--card)'

export type ChartTokens = {
  /** Series stroke width in px (`--chart-stroke`). */
  stroke: number
  /** Grid and reference line width in px (`--hairline`). */
  hairline: number
  /** Axis and label type size in px (`--text-small`). */
  fontSize: number
}

const DEFAULTS: ChartTokens = { stroke: 2, hairline: 1, fontSize: 12 }

function readTokens(): ChartTokens {
  if (typeof document === 'undefined') return DEFAULTS
  const style = getComputedStyle(document.documentElement)
  const px = (name: string, fallback: number) => {
    const raw = style.getPropertyValue(name).trim()
    if (!raw) return fallback
    // Tokens are declared in rem or px; rem resolves against the root font size.
    const n = parseFloat(raw)
    if (Number.isNaN(n)) return fallback
    return raw.endsWith('rem') ? n * parseFloat(style.fontSize || '16') : n
  }
  return {
    stroke: px('--chart-stroke', DEFAULTS.stroke),
    hairline: px('--hairline', DEFAULTS.hairline),
    fontSize: px('--text-small', DEFAULTS.fontSize),
  }
}

/**
 * The pixel values recharts needs as numbers, kept current across theme and density switches.
 * Both are applied as attributes on `<html>`, so one MutationObserver covers them.
 */
export function useChartTokens(): ChartTokens {
  const [tokens, setTokens] = useState<ChartTokens>(readTokens)

  useEffect(() => {
    const observer = new MutationObserver(() => setTokens(readTokens()))
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class', 'data-density'],
    })
    return () => observer.disconnect()
  }, [])

  return tokens
}

/** Props for an axis `tick` so labels use the muted text token at the small size. */
export const axisTick = (tokens: ChartTokens) => ({ fill: MUTED_TEXT, fontSize: tokens.fontSize })

/** Props shared by every axis: no axis line, no tick marks — the grid is enough. */
export const AXIS = { axisLine: false, tickLine: false } as const

/** Props for a label drawn inside the plot (reference lines, end values). */
export const plotLabel = (tokens: ChartTokens, color: string = MUTED_TEXT) => ({
  fill: color,
  fontSize: tokens.fontSize,
})
