import { useState } from 'react'
import { Link } from '@/components/Link'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { ago } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

export function Instances() {
  const [search, setSearch] = useState('')
  const { data, error } = useAdminLoad(() => api.instances(search), [search])

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-lg font-semibold">
          Instances{data ? <span className="ml-2 text-muted-foreground">{data.total}</span> : null}
        </h1>
        <Input
          type="search"
          aria-label="Search"
          placeholder="Search"
          className="max-w-xs"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {error && error.status !== 401 && <p className="text-destructive">{error.message}</p>}

      <Card className="gap-0 py-0">
        {!data ? (
          <div className="px-4 py-6 text-center text-muted-foreground">Loading</div>
        ) : data.items.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No instances</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">Instance</TableHead>
                <TableHead>URL</TableHead>
                <TableHead>Version</TableHead>
                <TableHead>Last seen</TableHead>
                <TableHead className="px-4">IP address</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((instance) => (
                <TableRow key={instance.instanceId}>
                  <TableCell className="px-4 font-mono">
                    <Link
                      href={`/admin/instances/${encodeURIComponent(instance.instanceId)}`}
                      className="text-primary hover:underline"
                    >
                      {instance.instanceId}
                    </Link>
                  </TableCell>
                  <TableCell className="font-mono">{instance.instanceUrl}</TableCell>
                  <TableCell className="font-mono">{instance.version ?? '—'}</TableCell>
                  <TableCell>{ago(instance.lastSeenAt)}</TableCell>
                  <TableCell className="px-4 font-mono">{instance.ipAddress ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </>
  )
}
