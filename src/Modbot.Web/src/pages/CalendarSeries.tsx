import { useCallback } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame, chartHeight, rechartsTooltip, seriesColor } from '@/components/charts'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { api, type CalendarSeries as Series } from '@/lib/api'
import { dateTime, formatDay } from '@/lib/format'
import { useLoad } from '@/lib/useLoad'

const NAMES = {
  discord: 'Interested on Discord',
  vrchat: 'Interested on VRChat',
  came: 'Came',
}

/** "12.5", or a dash when no date had the figure. */
function average(value: number | null): string {
  return value === null ? '—' : String(Math.round(value * 10) / 10)
}

function figure(value: number | null): string {
  return value === null ? '—' : String(value)
}

/** A count, with a muted "late" after it when it was read well after the date started. */
function counted(value: number | null, late: boolean) {
  return (
    <>
      {figure(value)}
      {value !== null && late && (
        <span className="ml-1 font-sans text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
          late
        </span>
      )}
    </>
  )
}

/**
 * One event's dates over time, at `/calendar/series/:id`: how many were interested on Discord and
 * on VRChat when each date started, and how many came. The first two are written once per date, so
 * a date from before Modbot kept them has a dash. "Came" counts only the people a moderator's
 * client saw arrive, so it is a floor, and a date nobody was seen at has a dash too.
 */
export function CalendarSeries({ eventId, onBack }: { eventId: string; onBack: () => void }) {
  const load = useCallback(() => api.calendarSeries(eventId), [eventId])
  const { data, error, reload } = useLoad(load)

  if (!data) {
    return (
      <Card className="w-full max-w-4xl">
        <EmptyRow tone={error ? 'danger' : 'loading'} onTryAgain={reload}>
          {error}
        </EmptyRow>
        <CardFooter>
          <Button variant="outline" size="xs" onClick={onBack}>
            Back to the calendar
          </Button>
        </CardFooter>
      </Card>
    )
  }

  return (
    <div className="flex w-full max-w-4xl flex-col gap-3">
      <PanelGrid className="grid-cols-1">
        <Card>
          <CardHeader>
            <CardTitle className="min-w-[5em] flex-1">{data.title}</CardTitle>
            <CardAction>
              <Button variant="outline" size="xs" onClick={onBack}>
                Back to the calendar
              </Button>
            </CardAction>
          </CardHeader>
          <CardContent className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {data.dates.length === 0 ? (
              'No dates yet.'
            ) : (
              <>
                Average per date: {average(data.averageDiscordInterested)} interested on Discord,{' '}
                {average(data.averageVRChatInterested)} interested on VRChat, {average(data.averageCame)} came.
              </>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Over time</CardTitle>
          </CardHeader>
          <CardContent>
            <Chart series={data} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Every date</CardTitle>
          </CardHeader>
          {data.dates.length === 0 ? (
            <EmptyRow onTryAgain={null}>Nothing recorded for this event yet.</EmptyRow>
          ) : (
            <Table
              head={
                <>
                  <Th>Date</Th>
                  <Th className="text-right">Interested on Discord</Th>
                  <Th className="text-right">Interested on VRChat</Th>
                  <Th className="text-right">Came</Th>
                  <Th className="text-right">Most at once</Th>
                </>
              }
            >
              {[...data.dates].reverse().map((d) => (
                <Tr key={d.startsAt}>
                  <Td>{dateTime(d.startsAt)}</Td>
                  <Td className="text-right font-mono">{counted(d.discordInterested, d.discordLate)}</Td>
                  <Td className="text-right font-mono">{counted(d.vrChatInterested, d.vrChatLate)}</Td>
                  <Td className="text-right font-mono">{figure(d.came)}</Td>
                  <Td className="text-right font-mono">{figure(d.mostAtOnce)}</Td>
                </Tr>
              ))}
            </Table>
          )}
        </Card>
      </PanelGrid>
    </div>
  )
}

/**
 * A line per count, one point per date, evenly spaced. Not `DailyLine`, which breaks the line at
 * every day without data: a weekly event has six empty days between its points, and a date that
 * lacks one count should skip it, not cut the line.
 */
function Chart({ series }: { series: Series }) {
  const rows = series.dates.map((d) => ({
    at: d.startsAt,
    discord: d.discordInterested,
    vrchat: d.vrChatInterested,
    came: d.came,
  }))

  return (
    <ChartFrame
      height={chartHeight.tall}
      empty={rows.length === 0}
      emptyText="Nothing recorded for this event yet."
      legend={[
        { label: NAMES.discord, slot: 1, sample: 'line' },
        { label: NAMES.vrchat, slot: 2, sample: 'line' },
        { label: NAMES.came, slot: 3, sample: 'line' },
      ]}
    >
      <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis
          dataKey="at"
          tickFormatter={(v: string) => formatDay(v)}
          tickLine={false}
          axisLine={false}
          minTickGap={16}
        />
        <YAxis width="auto" domain={[0, 'auto']} allowDecimals={false} tickLine={false} axisLine={false} />
        <Tooltip
          content={rechartsTooltip((label) => dateTime(label), {
            discord: NAMES.discord,
            vrchat: NAMES.vrchat,
            came: NAMES.came,
          })}
          cursor={{ stroke: 'var(--chart-grid)' }}
        />
        {(['discord', 'vrchat', 'came'] as const).map((key, i) => (
          <Line
            key={key}
            type="monotone"
            dataKey={key}
            name={NAMES[key]}
            stroke={seriesColor((i + 1) as 1 | 2 | 3)}
            strokeWidth={2}
            dot={{ r: 2.5 }}
            activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
            connectNulls
            isAnimationActive={false}
          />
        ))}
      </LineChart>
    </ChartFrame>
  )
}
