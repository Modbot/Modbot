import type { TooltipContentProps } from 'recharts'
import { ChartTooltip } from './ChartTooltip'

/**
 * Adapts Recharts' tooltip props to `ChartTooltip`. Pass the result as `content` on a `<Tooltip>`.
 *
 * `labelFormat` turns the x value into a title; `names` maps a series key to the words shown, so
 * a chart can key its data by a short id and still label it in plain language.
 */
export function rechartsTooltip(
  labelFormat: (label: string) => string,
  names: Record<string, string> = {},
  format?: (value: number) => string,
) {
  return function Content({ active, label, payload }: TooltipContentProps) {
    if (!active || !payload || payload.length === 0) return null

    return (
      <ChartTooltip
        title={labelFormat(String(label ?? ''))}
        format={format}
        rows={payload
          .filter((p) => p.value !== undefined && p.value !== null)
          .map((p) => ({
            name: names[String(p.dataKey ?? p.name ?? '')] ?? String(p.name ?? ''),
            value: Array.isArray(p.value) ? String(p.value) : (p.value as number | string),
            color: p.color,
          }))}
      />
    )
  }
}
