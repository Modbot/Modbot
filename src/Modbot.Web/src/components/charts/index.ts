/**
 * Charts, shared by every screen that draws one.
 *
 * Recharts underneath, themed through the CSS tokens in index.css (see charts.css and theme.ts)
 * so light, dark and VR all come out right without any chart knowing which it is in. The rules
 * the shapes follow, because they are easy to undo by accident:
 *
 * - Series colours are a fixed order (`SeriesSlot`), never cycled by rank. A filter that removes
 *   a series must not repaint the survivors.
 * - Marks are thin, grid lines are hairline and recessive, and nothing is dashed.
 * - Identity is never colour alone: every multi-series chart has a legend, and values are
 *   labelled in text.
 * - One axis, always. Two measures of different scale are two charts.
 * - Every chart has an empty state that says what would fill it.
 */
export { ChartFrame } from './ChartFrame'
export { ChartTooltip, type TooltipRow } from './ChartTooltip'
export { rechartsTooltip } from './rechartsTooltip'
export { DailyBars, type DaySeries } from './DailyBars'
export { DailyLine } from './DailyLine'
export { Heatmap } from './Heatmap'
export { RankedList, Legend } from './RankedList'
export { chartHeight, chartTheme, nextSlot, seriesColor, type SeriesSlot } from './theme'
export {
  compactNumber,
  dateTime,
  denseDays,
  longDay,
  mergeDays,
  minutes,
  percent,
  shortDay,
  tickDays,
  type DayPoint,
} from './format'
