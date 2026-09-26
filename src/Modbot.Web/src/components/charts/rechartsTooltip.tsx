import type { TooltipContentProps } from 'recharts'
import { ChartTooltip } from './ChartTooltip'
import type { DayMark } from './coverage'

/**
 * Adapts Recharts' tooltip props to `ChartTooltip`. Pass the result as `content` on a `<Tooltip>`.
 *
 * `labelFormat` turns the x value into a title; `names` maps a series key to the words shown, so
 * a chart can key its data by a short id and still label it in plain language. `ones` maps a key
 * to the words for a value of exactly one, for a name that is a plural noun: "1 person", not
 * "1 people".
 *
 * `mark` says what the day under the pointer is: a missing day says "No data" instead of numbers,
 * and a day not over yet says "so far" after its title. A series drawn in two parts (measured
 * solid, carried dashed) maps both keys to one name, and is listed once.
 */
export function rechartsTooltip(
  labelFormat: (label: string) => string,
  names: Record<string, string> = {},
  format?: (value: number) => string,
  ones: Record<string, string> = {},
  mark?: (label: string) => DayMark,
) {
  return function Content({ active, label, payload }: TooltipContentProps) {
    if (!active) return null

    const title = labelFormat(String(label ?? ''))
    const state = mark?.(String(label ?? ''))

    if (state === 'missing') return <ChartTooltip title={title} rows={[]} message="No data" />
    if (!payload || payload.length === 0) return null

    const seen = new Set<string>()

    return (
      <ChartTooltip
        title={title}
        format={format}
        note={state === 'today' ? 'so far' : undefined}
        rows={payload
          .filter((p) => p.value !== undefined && p.value !== null)
          .map((p) => ({
            name: names[String(p.dataKey ?? p.name ?? '')] ?? String(p.name ?? ''),
            one: ones[String(p.dataKey ?? p.name ?? '')],
            value: Array.isArray(p.value) ? String(p.value) : (p.value as number | string),
            color: p.color,
          }))
          .filter((row) => !seen.has(row.name) && Boolean(seen.add(row.name)))}
      />
    )
  }
}
