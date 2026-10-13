import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis, type TooltipContentProps } from 'recharts'
import { ChartFrame, ChartTooltip, chartHeight, compactNumber, dateTime, dayRange, longDay, percent, seriesColor, shortDay } from '@/components/charts'
import { SubjectLink } from '@/components/facts'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import type { RegularsAnalytics, WeekReturn } from '@/lib/api'
import { PageMessage, Panel, Stat, StatStrip } from './shared'

/** Below this many presence reports in the range, the numbers are shown but called thin. */
const THIN = 200

/**
 * The Regulars tab of the Stats page: do people come back, and who comes every week?
 *
 * A visitor in a week is anybody who joined any of the group's instances that week. They came back
 * if they joined a group instance again in the next four weeks, in any instance; the week's figure is
 * the share of visitors who did. A week shows nothing until those four weeks are over, so a figure
 * does not climb after it was drawn. A regular joined in at least three of the latest four whole
 * weeks. All of it comes from presence reports, so it only sees time a moderator's client was in the
 * instance (docs, Analytics, Regulars).
 */
export function RegularsStats({ data }: { data: RegularsAnalytics }) {
  const settled = data.weeks.filter((w) => w.cameBack !== null && w.visitors > 0)
  const latest = settled.at(-1)

  return (
    <PanelGrid className="grid-cols-1">
      {data.presenceReports === 0 ? (
        <PageMessage>No presence reports in this range.</PageMessage>
      ) : data.presenceReports < THIN ? (
        <PageMessage>
          Only <span className="font-mono">{compactNumber(data.presenceReports)}</span> presence reports in this range.
        </PageMessage>
      ) : null}

      <StatStrip className="md:grid-cols-3 xl:grid-cols-3">
        <Stat label="Regulars" value={compactNumber(data.regularsTotal)} />
        <Stat
          label="Came back"
          value={latest ? percent(latest.cameBack ?? 0, latest.visitors) : '—'}
          note={latest && `week of ${longDay(latest.weekStart)}`}
          noteMono
        />
        <Stat label="Visitors that week" value={latest ? compactNumber(latest.visitors) : '—'} />
      </StatStrip>

      <Panel title="Visitors who came back, per week">
        <ReturnChart weeks={data.weeks} />
      </Panel>

      <Panel
        title={`Joined in ${data.regularNeeds} of the last ${data.regularWeeks} weeks`}
        right={
          <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {dayRange(data.regularsFrom, data.regularsTo)}
          </span>
        }
        flush
      >
        {data.regulars.length === 0 ? (
          <EmptyRow>No regulars yet.</EmptyRow>
        ) : (
          <>
            <Table
              pinFirst
              head={
                <>
                  <Th>Person</Th>
                  <Th className="text-right">Weeks</Th>
                  <Th className="text-right">Days</Th>
                  <Th>Last joined</Th>
                </>
              }
            >
              {data.regulars.map((r) => (
                <Tr key={r.userId}>
                  <Td>
                    <SubjectLink id={r.userId} name={r.name} />
                  </Td>
                  <Td className="text-right font-mono">{r.weeks}</Td>
                  <Td className="text-right font-mono">{r.days}</Td>
                  <Td className="font-mono text-muted-foreground">{dateTime(r.lastSeenAt)}</Td>
                </Tr>
              ))}
            </Table>
            {data.regularsTotal > data.regulars.length && (
              <EmptyRow>
                {compactNumber(data.regulars.length)} of {compactNumber(data.regularsTotal)}
              </EmptyRow>
            )}
          </>
        )}
      </Panel>
    </PanelGrid>
  )
}

/**
 * The share of each week's visitors who came back, one point a week. A week not yet settled, or
 * with no visitors, has no point: the line breaks there rather than drop to nought.
 */
function ReturnChart({ weeks }: { weeks: WeekReturn[] }) {
  const rows = weeks.map((w) => ({
    week: w.weekStart,
    rate: w.cameBack !== null && w.visitors > 0 ? Math.round((w.cameBack / w.visitors) * 100) : null,
    visitors: w.visitors,
    cameBack: w.cameBack,
  }))
  const byWeek = new Map(rows.map((r) => [r.week, r]))
  const empty = rows.every((r) => r.rate === null)

  const tip = ({ active, label }: TooltipContentProps) => {
    const row = active ? byWeek.get(String(label ?? '')) : undefined
    if (!row) return null

    return (
      <ChartTooltip
        title={`Week of ${longDay(row.week)}`}
        rows={[
          ...(row.rate === null ? [] : [{ name: 'came back', value: `${row.rate}%`, color: seriesColor(1) }]),
          { name: 'visitors', one: 'visitor', value: row.visitors },
        ]}
      />
    )
  }

  return (
    <ChartFrame height={chartHeight.regular} empty={empty} emptyText="No finished weeks in this range.">
      <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="week" tickFormatter={shortDay} tickLine={false} axisLine={false} minTickGap={16} />
        <YAxis
          width="auto"
          domain={[0, 100]}
          tickFormatter={(v: number) => `${v}%`}
          tickLine={false}
          axisLine={false}
        />
        <Tooltip content={tip} cursor={{ stroke: 'var(--chart-grid)' }} />
        <Line
          type="monotone"
          dataKey="rate"
          stroke={seriesColor(1)}
          strokeWidth={2}
          dot={{ r: 3, fill: seriesColor(1), strokeWidth: 0 }}
          activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
          connectNulls={false}
          isAnimationActive={false}
        />
      </LineChart>
    </ChartFrame>
  )
}
