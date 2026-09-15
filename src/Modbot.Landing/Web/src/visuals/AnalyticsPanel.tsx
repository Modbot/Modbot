import { useState } from 'react'
import { cn } from '@/lib/utils'

/**
 * The four analytics pages, one tab each, with one of each page's real charts drawn over sample
 * numbers. Series colours are the app's fixed ones.
 */
type Page = 'group' | 'team' | 'worlds' | 'instances'

const PAGES: { value: Page; label: string }[] = [
  { value: 'group', label: 'My Group' },
  { value: 'team', label: 'My Team' },
  { value: 'worlds', label: 'Worlds' },
  { value: 'instances', label: 'Instances' },
]

export function AnalyticsPanel() {
  const [page, setPage] = useState<Page>('instances')

  return (
    <div className="app overflow-hidden rounded-xl border bg-card text-card-foreground shadow-[0_30px_80px_-40px_rgb(22_24_31/0.35)]">
      <div role="tablist" aria-label="Analytics pages" className="flex overflow-x-auto overflow-y-hidden border-b px-2">
        {PAGES.map((p) => (
          <button
            key={p.value}
            type="button"
            role="tab"
            id={`analytics-${p.value}`}
            aria-selected={page === p.value}
            aria-controls="analytics-panel"
            onClick={() => setPage(p.value)}
            className={cn(
              'relative shrink-0 px-3 py-2.5 font-medium transition-colors',
              page === p.value ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {p.label}
            {page === p.value && <span className="absolute inset-x-2 bottom-0 h-0.5 rounded-full bg-foreground" />}
          </button>
        ))}
      </div>
      <div id="analytics-panel" role="tabpanel" aria-labelledby={`analytics-${page}`} className="p-4 sm:p-5">
        {page === 'group' && <Group />}
        {page === 'team' && <Team />}
        {page === 'worlds' && <Worlds />}
        {page === 'instances' && <Instances />}
      </div>
    </div>
  )
}

function Stats({ stats }: { stats: [string, string][] }) {
  return (
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
      {stats.map(([label, value]) => (
        <div key={label} className="rounded-md border px-3 py-2">
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>{label}</div>
          <div className="mt-0.5 font-mono text-lg font-medium tabular-nums">{value}</div>
        </div>
      ))}
    </div>
  )
}

function ChartTitle({ children }: { children: React.ReactNode }) {
  return <div className="mt-5 mb-2 font-medium">{children}</div>
}

/** Bars around a zero line: joins above, leaves below. */
function JoinsAndLeaves() {
  const joins = [12, 8, 15, 9, 22, 30, 18, 11, 14, 10, 19, 27, 24, 16]
  const leaves = [4, 6, 3, 7, 5, 9, 8, 4, 6, 5, 7, 6, 10, 5]
  const max = 30
  return (
    <svg viewBox="0 0 280 120" className="h-40 w-full" role="img" aria-label="Joins and leaves per day, sample numbers">
      <line x1="0" x2="280" y1="80" y2="80" stroke="var(--chart-grid)" />
      {joins.map((j, i) => (
        <rect key={`j${i}`} x={i * 20 + 3} width="14" y={80 - (j / max) * 72} height={(j / max) * 72} rx="2" fill="var(--series-3)" />
      ))}
      {leaves.map((l, i) => (
        <rect key={`l${i}`} x={i * 20 + 3} width="14" y="81" height={(l / max) * 72} rx="2" fill="var(--series-2)" />
      ))}
    </svg>
  )
}

function Group() {
  return (
    <>
      <Stats stats={[['Members', '1,284'], ['Joined', '215'], ['Left', '85'], ['Net change', '+130']]} />
      <ChartTitle>Joins and leaves per day</ChartTitle>
      <JoinsAndLeaves />
    </>
  )
}

function Team() {
  const rows: [string, string, string, string, string][] = [
    ['Sat 23:10', '42m', '11', 'Wren', 'a moderator arrived'],
    ['Fri 01:25', '1h 05m', '6', 'Oto', 'the instance was closed'],
    ['Thu 22:02', '18m', '4', 'Wren', 'a moderator arrived'],
  ]
  return (
    <>
      <Stats stats={[['Moderators active', '7'], ['Actions', '63'], ['Coverage gaps', '9'], ['Instances nobody watched', '4']]} />
      <ChartTitle>Coverage gaps</ChartTitle>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[30rem]" style={{ fontSize: 'var(--text-small)' }}>
          <thead className="text-left text-muted-foreground">
            <tr>
              <th className="py-1 pr-3 font-medium">Began</th>
              <th className="py-1 pr-3 text-right font-medium">Lasted</th>
              <th className="py-1 pr-3 text-right font-medium">People left behind</th>
              <th className="py-1 pr-3 font-medium">Last moderator out</th>
              <th className="py-1 font-medium">Ended because</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r[0]} className="border-t">
                <td className="py-1.5 pr-3">{r[0]}</td>
                <td className="py-1.5 pr-3 text-right tabular-nums">{r[1]}</td>
                <td className="py-1.5 pr-3 text-right tabular-nums">{r[2]}</td>
                <td className="py-1.5 pr-3 font-medium">{r[3]}</td>
                <td className="py-1.5 text-muted-foreground">{r[4]}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

function Worlds() {
  const lines = [
    { name: 'Lantern Harbor', slot: 1, points: [20, 24, 22, 31, 35, 44, 40, 29, 33, 30, 38, 45, 52, 47] },
    { name: 'Quiet Orbit Lounge', slot: 3, points: [12, 10, 15, 14, 18, 22, 25, 17, 16, 19, 21, 24, 27, 26] },
    { name: 'Pixel Karaoke Hall', slot: 4, points: [6, 9, 5, 8, 14, 20, 12, 7, 9, 8, 11, 17, 19, 13] },
  ]
  const path = (points: number[]) => points.map((p, i) => `${i === 0 ? 'M' : 'L'}${(i / 13) * 276 + 2},${110 - (p / 55) * 100}`).join(' ')
  return (
    <>
      <Stats stats={[['Worlds', '23'], ['Time seen', '1,940h'], ['Visitors', '812'], ['Presence reports', '96k']]} />
      <ChartTitle>Visitors per day, busiest worlds</ChartTitle>
      <svg viewBox="0 0 280 115" className="h-40 w-full" role="img" aria-label="Visitors per day for the three busiest worlds, sample numbers">
        {[10, 60, 110].map((y) => (
          <line key={y} x1="0" x2="280" y1={y} y2={y} stroke="var(--chart-grid)" />
        ))}
        {lines.map((l) => (
          <path key={l.name} d={path(l.points)} fill="none" stroke={`var(--series-${l.slot})`} strokeWidth="2" strokeLinejoin="round" />
        ))}
      </svg>
      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
        {lines.map((l) => (
          <span key={l.name} className="flex items-center gap-1.5">
            <span className="h-0.5 w-3 rounded-full" style={{ background: `var(--series-${l.slot})` }} />
            {l.name}
          </span>
        ))}
      </div>
    </>
  )
}

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

/** How many instances were open, by weekday and hour. A made-up week that peaks at weekend nights. */
function busy(day: number, hour: number): number {
  const away = Math.min(Math.abs(hour - 21), 24 - Math.abs(hour - 21))
  const evening = Math.max(0, 1 - away / 5)
  const weekend = day >= 4 ? 1 : 0.55
  const wobble = ((day * 7 + hour * 13) % 5) / 20
  return Math.min(1, evening * weekend + wobble * evening)
}

function Instances() {
  return (
    <>
      <Stats stats={[['Opened', '148'], ['Closed', '146'], ['Typical time open', '2h 10m'], ['Most open at once', '5']]} />
      <ChartTitle>When instances are open</ChartTitle>
      <div className="overflow-x-auto">
        <div className="grid min-w-[30rem] grid-cols-[2.25rem_repeat(24,minmax(0,1fr))] gap-[3px]" role="img" aria-label="Heatmap of open instances by day and hour, sample numbers">
          {DAYS.map((day, d) => (
            <div key={day} className="contents">
              <div className="self-center text-muted-foreground" style={{ fontSize: '11px' }}>{day}</div>
              {Array.from({ length: 24 }, (_, h) => (
                <div
                  key={h}
                  className="aspect-square rounded-[3px]"
                  style={{ background: `color-mix(in oklab, var(--series-1) ${Math.round(8 + busy(d, h) * 92)}%, var(--secondary))` }}
                />
              ))}
            </div>
          ))}
          <div />
          {Array.from({ length: 24 }, (_, h) => (
            <div key={h} className="text-center text-muted-foreground" style={{ fontSize: '10px' }}>
              {h % 6 === 0 ? String(h).padStart(2, '0') : ''}
            </div>
          ))}
        </div>
      </div>
    </>
  )
}
