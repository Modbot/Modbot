import { useState } from 'react'
import { Link } from '@/components/Link'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api, type DayCount } from '@/lib/api'
import { ago, clockText, when } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

const PAGE = 50

export function Installs() {
  const [offset, setOffset] = useState(0)
  const days = useAdminLoad(() => api.eventsPerDay(30), [])
  const { data, error } = useAdminLoad(() => api.installs(offset, PAGE), [offset])

  return (
    <>
      <h1 className="font-display text-lg">Installs</h1>

      <Card className="gap-0 py-0">
        <h2 className="font-display border-b px-4 py-3 text-base">Events per day</h2>
        <div className="px-4 py-4">
          {days.error && days.error.status !== 401 ? (
            <p className="text-destructive">{days.error.message}</p>
          ) : days.data ? (
            <EventsChart days={days.data.items} />
          ) : (
            <p className="text-muted-foreground">Loading</p>
          )}
        </div>
      </Card>

      <Card className="gap-0 py-0">
        {error && error.status !== 401 ? (
          <p className="px-4 py-6 text-destructive">{error.message}</p>
        ) : !data ? (
          <p className="px-4 py-6 text-muted-foreground">Loading</p>
        ) : data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No installs</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">Install</TableHead>
                <TableHead className="px-4">Version</TableHead>
                <TableHead className="px-4">First seen</TableHead>
                <TableHead className="px-4">Last seen</TableHead>
                <TableHead className="px-4 text-right">Events stored</TableHead>
                <TableHead className="px-4 text-right">Clock offset</TableHead>
                <TableHead className="px-4">Paired server</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((install) => (
                <TableRow key={install.installId}>
                  <TableCell className="px-4 font-mono">
                    <Link href={`/admin/installs/${install.installId}`} className="underline-offset-2 hover:underline">
                      {install.installId.slice(0, 8)}
                    </Link>
                  </TableCell>
                  <TableCell className="px-4 font-mono">{install.companionVersion}</TableCell>
                  <TableCell className="px-4" title={when(install.firstSeenAt)}>
                    {ago(install.firstSeenAt)}
                  </TableCell>
                  <TableCell className="px-4" title={when(install.lastSeenAt)}>
                    {ago(install.lastSeenAt)}
                  </TableCell>
                  <TableCell className="px-4 text-right">{install.eventsStored.toLocaleString()}</TableCell>
                  <TableCell className="px-4 text-right">
                    {clockText(install.clockOffsetMs)}
                    {install.clockDisagrees && (
                      <Badge variant="destructive" className="ml-2">
                        Disagrees
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell className="px-4 font-mono">{install.modbotServerId ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      {data && data.total > PAGE && (
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
            Previous
          </Button>
          <span className="text-muted-foreground">
            {offset + 1}–{Math.min(offset + PAGE, data.total)} of {data.total}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={offset + PAGE >= data.total}
            onClick={() => setOffset(offset + PAGE)}
          >
            Next
          </Button>
        </div>
      )}
    </>
  )
}

/** One bar per day, scaled to the busiest day. Plain SVG: one chart does not need a library. */
function EventsChart({ days }: { days: DayCount[] }) {
  const most = Math.max(1, ...days.map((d) => d.events))
  const width = 720
  const height = 140
  const gap = 2
  const bar = days.length > 0 ? width / days.length : width

  return (
    <div className="flex flex-col gap-1">
      <svg viewBox={`0 0 ${width} ${height}`} className="h-36 w-full" role="img" aria-label="Events per day">
        {days.map((d, i) => {
          const h = Math.round((d.events / most) * (height - 4))
          return (
            <rect
              key={d.day}
              x={i * bar + gap / 2}
              y={height - h}
              width={Math.max(1, bar - gap)}
              height={h}
              rx={2}
              className="fill-primary"
            >
              <title>{`${d.day}: ${d.events.toLocaleString()}`}</title>
            </rect>
          )
        })}
      </svg>
      <div className="flex justify-between text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span>{days[0]?.day}</span>
        <span>{most.toLocaleString()}</span>
        <span>{days[days.length - 1]?.day}</span>
      </div>
    </div>
  )
}
