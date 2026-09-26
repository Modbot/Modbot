import { useEffect, useState } from 'react'
import { CartesianGrid, Line, LineChart, Tooltip, XAxis, YAxis } from 'recharts'
import { ChartFrame, chartHeight, rechartsTooltip, seriesColor } from '@/components/charts'
import { api, type MachineUsage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { readingTime, timeLabel, timeTicks } from '@/pages/analytics/memberCountSeries'
import { SettingsCard } from './SettingsCard'
import {
  latest,
  memoryValue,
  percent,
  perSecond,
  processorTop,
  readable,
  toRows,
  type UsageRow,
} from './machineUsage'
import { bytes } from './units'

/** A figure as text, or a dash where the newest reading does not carry it. */
const shown = (n: number | null, format: (v: number) => string) => (n === null ? '—' : format(n))

/**
 * How hard this machine is working: processor, memory and disk, over the last half hour.
 *
 * Every figure is this server's own use of the machine, which is the only thing it can measure
 * without being told about its host — and in a container it is also the useful answer, because the
 * container is what the operator sized.
 *
 * The card only draws figures the host actually reports. The disk counters are Linux-only and a
 * memory limit exists only where something sets one, so on a Windows machine, or for the first ten
 * seconds after a restart, what cannot be read is left out rather than drawn as a flat zero. When
 * nothing at all can be read — including before the second reading has been taken, since a rate
 * needs two — the card is not there.
 *
 * It refreshes at the rate the server samples, so a screen left open follows the machine.
 */
export function MachineUsageCard() {
  const [data, setData] = useState<MachineUsage | null>(null)

  const every = data?.sampleSeconds ?? 10

  useEffect(() => {
    let stopped = false

    const load = () =>
      api
        .machineUsage()
        .then((next) => {
          if (!stopped) setData(next)
        })
        // A diagnostic nobody can read is not worth a line of its own on a screen full of
        // settings: no permission, or no answer, means the card simply is not there.
        .catch(() => {})

    void load()
    const timer = window.setInterval(() => void load(), every * 1000)

    return () => {
      stopped = true
      window.clearInterval(timer)
    }
  }, [every])

  if (!data) return null

  const rows = toRows(data.points)

  const processor = readable(rows, 'processor')
  const memory = readable(rows, 'memory')
  const disk = readable(rows, 'diskRead') || readable(rows, 'diskWrite')

  if (!processor && !memory && !disk) return null

  const from = rows.length > 0 ? rows[0].at : 0
  const to = rows.length > 0 ? rows[rows.length - 1].at : 0

  const held = latest(rows, 'memory')
  const busiest = Math.max(0, ...rows.map((r) => r.processor ?? 0))
  const mostMemory = Math.max(0, ...rows.map((r) => r.memory ?? 0))
  const limit = data.memoryLimitBytes

  // As many columns as there are charts, so a host that cannot report its disk leaves no empty
  // third on the right.
  const charts = [processor, memory, disk].filter(Boolean).length
  const columns = charts === 3 ? 'lg:grid-cols-3' : charts === 2 ? 'lg:grid-cols-2' : ''

  return (
    <SettingsCard span={12} title="Machine usage">
      <div className={cn('grid gap-6', columns)}>
        {processor && (
          <Usage
            label="Processor"
            rows={rows}
            from={from}
            to={to}
            top={processorTop(busiest)}
            format={percent}
            lines={[
              {
                key: 'processor',
                label: 'Processor',
                slot: 1,
                value: shown(latest(rows, 'processor'), percent),
              },
            ]}
          />
        )}

        {memory && (
          <Usage
            label="Memory"
            rows={rows}
            from={from}
            to={to}
            // Against the limit when there is one, so the chart answers how close this server is
            // to the memory it was given rather than only how its own use moved.
            top={limit !== null && limit > 0 ? limit : Math.max(mostMemory, 1)}
            format={bytes}
            lines={[
              {
                key: 'memory',
                label: 'Memory',
                slot: 2,
                value: held === null ? '—' : memoryValue(held, limit),
              },
            ]}
          />
        )}

        {disk && (
          <Usage
            label="Disk"
            rows={rows}
            from={from}
            to={to}
            top="auto"
            format={perSecond}
            lines={[
              ...(readable(rows, 'diskRead')
                ? [
                    {
                      key: 'diskRead' as const,
                      label: 'Read',
                      slot: 3 as const,
                      value: shown(latest(rows, 'diskRead'), perSecond),
                    },
                  ]
                : []),
              ...(readable(rows, 'diskWrite')
                ? [
                    {
                      key: 'diskWrite' as const,
                      label: 'Write',
                      slot: 4 as const,
                      value: shown(latest(rows, 'diskWrite'), perSecond),
                    },
                  ]
                : []),
            ]}
          />
        )}
      </div>
    </SettingsCard>
  )
}

type UsageLine = {
  key: keyof Omit<UsageRow, 'at'>
  label: string
  slot: 1 | 2 | 3 | 4 | 5
  value: string
}

/**
 * One figure: its name, its current value, and the window as a line.
 *
 * A single-series chart is named once, above the plot; the two-series disk chart names each line
 * beside its own value, because identity here must not ride on colour alone.
 */
function Usage({
  label,
  rows,
  from,
  to,
  top,
  format,
  lines,
}: {
  label: string
  rows: UsageRow[]
  from: number
  to: number
  top: number | 'auto'
  format: (value: number) => string
  lines: UsageLine[]
}) {
  const names = Object.fromEntries(lines.map((l) => [l.key, l.label]))
  const single = lines.length === 1

  return (
    <div className="flex min-w-0 flex-col gap-2">
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </div>

      {single && <span className="font-mono font-medium tabular-nums">{lines[0].value}</span>}

      <ChartFrame
        height={chartHeight.regular}
        legend={single ? undefined : lines.map((l) => ({ label: l.label, slot: l.slot, value: l.value }))}
      >
        <LineChart data={rows} margin={{ top: 6, right: 8, bottom: 0, left: 0 }}>
          <CartesianGrid vertical={false} />
          <XAxis
            dataKey="at"
            type="number"
            domain={[from, to]}
            ticks={timeTicks(from, to, 4)}
            tickFormatter={(v: number) => timeLabel(v, to - from)}
            tickLine={false}
            axisLine={false}
            minTickGap={16}
          />
          <YAxis
            width="auto"
            domain={[0, top]}
            tickFormatter={format}
            tickLine={false}
            axisLine={false}
          />
          <Tooltip
            content={rechartsTooltip((at) => readingTime(Number(at)), names, format)}
            cursor={{ stroke: 'var(--chart-grid)' }}
          />
          {lines.map((line) => (
            <Line
              key={line.key}
              type="monotone"
              dataKey={line.key}
              name={line.label}
              stroke={seriesColor(line.slot)}
              strokeWidth={2}
              dot={false}
              activeDot={{ r: 4, strokeWidth: 2, stroke: 'var(--card)' }}
              connectNulls
              isAnimationActive={false}
            />
          ))}
        </LineChart>
      </ChartFrame>
    </div>
  )
}
