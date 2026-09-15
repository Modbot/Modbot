import { Card } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { clockText, when } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

const EVENTS = 200

/**
 * One install's recent events, for debugging. Every value is rendered as plain text, never as a
 * link: world and instance stay two separate strings, and nothing here builds a join link from them.
 */
export function InstallDetail({ installId }: { installId: string }) {
  const install = useAdminLoad(() => api.install(installId), [installId])
  const events = useAdminLoad(() => api.events(installId, EVENTS), [installId])

  if (install.error && install.error.status !== 401) return <p className="text-destructive">{install.error.message}</p>
  if (!install.data) return <p className="text-muted-foreground">Loading</p>

  const i = install.data
  const facts = [
    { label: 'Version', value: i.clientVersion },
    { label: 'First seen', value: when(i.firstSeenAt) },
    { label: 'Last seen', value: when(i.lastSeenAt) },
    { label: 'Events stored', value: i.eventsStored.toLocaleString() },
    { label: 'Clock offset', value: clockText(i.clockOffsetMs) + (i.clockDisagrees ? ' (disagrees)' : '') },
    { label: 'Paired server', value: i.modbotServerId ?? '—' },
  ]

  return (
    <>
      <h1 className="font-mono text-lg font-semibold">{i.installId}</h1>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
        {facts.map((fact) => (
          <Card key={fact.label} className="gap-1 px-4 py-4">
            <div className="text-muted-foreground">{fact.label}</div>
            <div className="font-semibold break-words">{fact.value}</div>
          </Card>
        ))}
      </div>

      <Card className="gap-0 py-0">
        <h2 className="border-b px-4 py-3 font-semibold">Recent events</h2>
        {events.error && events.error.status !== 401 ? (
          <p className="px-4 py-6 text-destructive">{events.error.message}</p>
        ) : !events.data ? (
          <p className="px-4 py-6 text-muted-foreground">Loading</p>
        ) : events.data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No events</div>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="px-4">Received</TableHead>
                  <TableHead className="px-4">Happened</TableHead>
                  <TableHead className="px-4">Type</TableHead>
                  <TableHead className="px-4">Player</TableHead>
                  <TableHead className="px-4">World</TableHead>
                  <TableHead className="px-4">Instance</TableHead>
                  <TableHead className="px-4">Group</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {events.data.items.map((e) => (
                  <TableRow key={e.clientEventId}>
                    <TableCell className="px-4 whitespace-nowrap">{when(e.receivedAt)}</TableCell>
                    <TableCell className="px-4 whitespace-nowrap">{when(e.occurredAt)}</TableCell>
                    <TableCell className="px-4 font-mono whitespace-nowrap">
                      {e.type}
                      {e.typeRaw && ` (${e.typeRaw})`}
                    </TableCell>
                    <TableCell className="px-4">
                      {e.displayName && <div>{e.displayName}</div>}
                      <div className="font-mono text-muted-foreground break-all">{e.subjectId}</div>
                    </TableCell>
                    <TableCell className="px-4 font-mono break-all">{e.worldId}</TableCell>
                    <TableCell className="px-4 font-mono break-all">{e.instanceId}</TableCell>
                    <TableCell className="px-4 font-mono break-all">{e.groupId ?? '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </Card>
    </>
  )
}
