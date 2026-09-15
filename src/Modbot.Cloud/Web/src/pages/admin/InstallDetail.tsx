import { Card } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { clockText, when } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

const LINES = 200

/**
 * One install's recent lines, for debugging. Lines are rendered as plain text, never as links: the
 * server has already hidden instance nonces, and nothing here turns a location into a join link.
 */
export function InstallDetail({ installId }: { installId: string }) {
  const install = useAdminLoad(() => api.install(installId), [installId])
  const lines = useAdminLoad(() => api.lines(installId, LINES), [installId])

  if (install.error && install.error.status !== 401) return <p className="text-destructive">{install.error.message}</p>
  if (!install.data) return <p className="text-muted-foreground">Loading</p>

  const i = install.data
  const facts = [
    { label: 'Version', value: i.clientVersion },
    { label: 'First seen', value: when(i.firstSeenAt) },
    { label: 'Last seen', value: when(i.lastSeenAt) },
    { label: 'Lines stored', value: i.linesStored.toLocaleString() },
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
        <h2 className="border-b px-4 py-3 font-semibold">Recent lines</h2>
        {lines.error && lines.error.status !== 401 ? (
          <p className="px-4 py-6 text-destructive">{lines.error.message}</p>
        ) : !lines.data ? (
          <p className="px-4 py-6 text-muted-foreground">Loading</p>
        ) : lines.data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No lines</div>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="px-4">Received</TableHead>
                  <TableHead className="px-4">Sent</TableHead>
                  <TableHead className="px-4">Logged</TableHead>
                  <TableHead className="px-4">File</TableHead>
                  <TableHead className="px-4 text-right">Offset</TableHead>
                  <TableHead className="px-4">Line</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {lines.data.items.map((line) => (
                  <TableRow key={`${line.file}:${line.offset}:${line.receivedAt}`}>
                    <TableCell className="px-4 whitespace-nowrap">{when(line.receivedAt)}</TableCell>
                    <TableCell className="px-4 whitespace-nowrap">{when(line.sentAt)}</TableCell>
                    <TableCell className="px-4 font-mono whitespace-nowrap">
                      {line.loggedAt ?? '—'}
                      {line.utcOffsetMinutes !== null && ` ${line.utcOffsetMinutes >= 0 ? '+' : ''}${line.utcOffsetMinutes}m`}
                    </TableCell>
                    <TableCell className="px-4 font-mono whitespace-nowrap">{line.file}</TableCell>
                    <TableCell className="px-4 text-right font-mono">{line.offset}</TableCell>
                    <TableCell className="px-4 font-mono break-all whitespace-pre-wrap">{line.text}</TableCell>
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
