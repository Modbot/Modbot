import type { ReactNode } from 'react'
import { DeleteButton } from '@/components/DeleteButton'
import { Link } from '@/components/Link'
import { Card } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { when } from '@/lib/format'
import { go } from '@/lib/router'
import { useAdminLoad } from '@/lib/useLoad'

const yesNo = (value: boolean | null) => (value === null ? '—' : value ? 'Yes' : 'No')
const count = (value: number | null) => (value === null ? '—' : String(value))

export function InstanceDetail({ instanceId }: { instanceId: string }) {
  const instance = useAdminLoad(() => api.instance(instanceId), [instanceId])
  const history = useAdminLoad(() => api.ipHistory(instanceId), [instanceId])

  const back = (
    <Link href="/admin/instances" className="text-muted-foreground hover:text-foreground">
      Instances
    </Link>
  )

  if (instance.error?.status === 404) {
    return (
      <>
        {back}
        <h1 className="text-lg font-semibold">Instance not found</h1>
      </>
    )
  }

  if (instance.error && instance.error.status !== 401) return <p className="text-destructive">{instance.error.message}</p>

  const data = instance.data
  if (!data) return <p className="text-muted-foreground">Loading</p>

  const fields: [string, ReactNode][] = [
    ['URL', <a key="url" href={data.instanceUrl} className="font-mono text-primary hover:underline">{data.instanceUrl}</a>],
    ['Version', <span key="version" className="font-mono">{data.version ?? '—'}</span>],
    ['Registered', when(data.registeredAt)],
    ['Last seen', when(data.lastSeenAt)],
    ['IP address', <span key="ip" className="font-mono">{data.ipAddress ?? '—'}</span>],
    ['Analytics', yesNo(data.analyticsEnabled)],
    ['Last usage report', when(data.lastUsageReportAt)],
    ['Scale', data.scaleBucket ?? '—'],
    ['Paired clients', count(data.pairedClients)],
    ['Discord connected', yesNo(data.discordConnected)],
    ['Term lists', data.termListsImported?.length ? data.termListsImported.join(', ') : '—'],
    ['Rate limit cold stops', count(data.rateLimitColdStops)],
    ['WAF blocks', count(data.wafBlocks)],
  ]

  return (
    <>
      {back}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="break-all font-mono text-lg font-semibold">{data.instanceId}</h1>
        <DeleteButton
          onConfirm={async () => {
            await api.deleteInstance(data.instanceId)
            go('/admin/instances')
          }}
        />
      </div>

      <Card className="py-0">
        <dl className="grid grid-cols-1 sm:grid-cols-[12rem_1fr]">
          {fields.map(([label, value]) => (
            <div key={label} className="contents">
              <dt className="border-t px-4 pt-2.5 text-muted-foreground first-of-type:border-t-0 sm:py-2.5">{label}</dt>
              <dd className="break-all border-t-0 px-4 pb-2.5 sm:border-t sm:py-2.5">{value}</dd>
            </div>
          ))}
        </dl>
      </Card>

      <Card className="gap-0 py-0">
        <h2 className="border-b px-4 py-3 font-semibold">IP history</h2>
        {!history.data ? (
          <div className="px-4 py-6 text-center text-muted-foreground">Loading</div>
        ) : history.data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No addresses</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">IP address</TableHead>
                <TableHead>First seen</TableHead>
                <TableHead>Last seen</TableHead>
                <TableHead className="px-4 text-right">Requests</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {history.data.items.map((row) => (
                <TableRow key={row.ipAddress}>
                  <TableCell className="px-4 font-mono">{row.ipAddress}</TableCell>
                  <TableCell>{when(row.firstSeenAt)}</TableCell>
                  <TableCell>{when(row.lastSeenAt)}</TableCell>
                  <TableCell className="px-4 text-right">{row.requests}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </>
  )
}
