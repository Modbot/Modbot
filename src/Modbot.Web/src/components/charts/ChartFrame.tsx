import { ResponsiveContainer } from 'recharts'
import './charts.css'
import { chartHeight } from './theme'

/**
 * The box every chart sits in: a fixed height (Recharts measures its parent, and a parent with no
 * height measures as zero), the theme hooks from charts.css, and one honest empty state.
 *
 * `empty` is decided by the caller, because only the caller knows what "nothing" means for its
 * data -- an empty series and a series of zeros are different answers. The empty text is a short
 * statement, not an explanation of what would fill the chart (CLAUDE.md, "UI text").
 */
export function ChartFrame({
  height = chartHeight.regular,
  empty = false,
  emptyText = 'Nothing recorded in this range.',
  children,
}: {
  height?: number
  empty?: boolean
  emptyText?: string
  children: React.ReactElement
}) {
  if (empty) {
    return (
      <div
        className="grid rounded-xl border border-dashed text-muted-foreground"
        style={{ height, placeItems: 'center', fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
      >
        <p className="max-w-md px-4 text-center">{emptyText}</p>
      </div>
    )
  }

  return (
    <div className="modbot-chart" style={{ height, width: '100%' }}>
      <ResponsiveContainer width="100%" height="100%">
        {children}
      </ResponsiveContainer>
    </div>
  )
}
