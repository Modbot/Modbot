import type { MachineUsagePoint } from '@/lib/api'
import { bytes } from './units.ts'

/**
 * The machine usage charts' plain functions: their rows, which figures the host can read, and how
 * a value is written. No components, so the tests run them under node and the card keeps its
 * fast-refresh boundary.
 */

/** One chart row. The time is a number because the axis is a number line. */
export type UsageRow = {
  at: number
  processor: number | null
  memory: number | null
  diskRead: number | null
  diskWrite: number | null
}

/** Which figure a chart is drawing. */
export type Figure = 'processor' | 'memory' | 'diskRead' | 'diskWrite'

export function toRows(points: MachineUsagePoint[]): UsageRow[] {
  return points
    .map((p) => ({
      at: Date.parse(p.at),
      processor: p.processorPercent,
      memory: p.memoryBytes,
      diskRead: p.diskReadBytesPerSecond,
      diskWrite: p.diskWrittenBytesPerSecond,
    }))
    .filter((r) => Number.isFinite(r.at))
    .sort((a, b) => a.at - b.at)
}

/**
 * Whether this host reads a figure at all.
 *
 * A figure nothing can read is left off the screen entirely. Drawing an empty chart, or a line
 * along zero, would say the machine is idle when what is true is that nobody knows.
 */
export function readable(rows: UsageRow[], figure: Figure): boolean {
  return rows.some((r) => r[figure] !== null)
}

/** The newest value of a figure, or null when there is none. */
export function latest(rows: UsageRow[], figure: Figure): number | null {
  for (let i = rows.length - 1; i >= 0; i--) {
    const value = rows[i][figure]
    if (value !== null) return value
  }
  return null
}

/**
 * The top of the processor axis: the next ten percent above the busiest reading, and never less
 * than ten.
 *
 * A floor stops an idle server's noise between one and two percent being stretched into a chart
 * that looks like a machine in trouble; growing in tens keeps a busy one against a scale a reader
 * can compare with the last time they looked.
 */
export function processorTop(busiest: number): number {
  if (!Number.isFinite(busiest) || busiest <= 0) return 10
  return Math.min(100, Math.max(10, Math.ceil(busiest / 10) * 10))
}

/** A whole-number percentage. */
export function percent(value: number): string {
  return `${Math.round(value)}%`
}

/** Bytes a second, in whichever unit keeps it short. */
export function perSecond(value: number): string {
  return `${bytes(Math.round(value))}/s`
}

/** Memory held, against the limit when there is one. */
export function memoryValue(held: number, limit: number | null): string {
  return limit !== null && limit > 0 ? `${bytes(held)} of ${bytes(limit)}` : bytes(held)
}
