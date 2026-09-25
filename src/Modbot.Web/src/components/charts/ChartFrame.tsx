import { ResponsiveContainer } from 'recharts'
import { EmptyRow } from '@/components/PanelGrid'
import './charts.css'
import { Legend } from './RankedList'
import { chartHeight, type SeriesSlot } from './theme'

/**
 * The box every chart sits in: a fixed height (Recharts measures its parent, and a parent with no
 * height measures as zero), the theme hooks from charts.css, and one honest empty state.
 *
 * `empty` is decided by the caller, because only the caller knows what "nothing" means for its
 * data -- an empty series and a series of zeros are different answers. The empty text is a short
 * statement, not an explanation of what would fill the chart (CLAUDE.md, "UI text").
 *
 * An empty chart is one empty row and nothing else: no plot height held open around a sentence,
 * and no legend, since a key to lines that are not drawn is noise. That is why the legend is given
 * to the chart rather than drawn above it by the page. The row is as high as the empty row of a
 * flush panel once the panel's inset around it is counted, so a chart panel with nothing in it is
 * the same height as any other empty panel.
 */
export function ChartFrame({
  height = chartHeight.regular,
  empty = false,
  emptyText = 'Nothing recorded in this range.',
  legend,
  children,
}: {
  height?: number
  empty?: boolean
  emptyText?: string
  /** The series' names and colours, drawn above the plot only when there is a plot. */
  legend?: { label: string; slot: SeriesSlot }[]
  children: React.ReactElement
}) {
  if (empty) {
    return (
      <EmptyRow className="px-0" minHeight="calc(var(--row-h) * 1.5 - 2 * var(--panel-pad))">
        {emptyText}
      </EmptyRow>
    )
  }

  const plot = (
    <div className="modbot-chart" style={{ height, width: '100%' }}>
      <ResponsiveContainer width="100%" height="100%">
        {children}
      </ResponsiveContainer>
    </div>
  )

  if (!legend) return plot

  return (
    <div className="flex flex-col gap-2">
      <Legend items={legend} />
      {plot}
    </div>
  )
}
