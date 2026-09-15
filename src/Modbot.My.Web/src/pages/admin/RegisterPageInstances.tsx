import { useState } from 'react'
import { DeleteButton } from '@/components/DeleteButton'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { when } from '@/lib/format'
import { useAdminLoad } from '@/lib/useLoad'

export function RegisterPageInstances() {
  const [search, setSearch] = useState('')
  const { data, error, reload } = useAdminLoad(() => api.registerPageInstances(search), [search])

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-lg font-display">
          Register page{data ? <span className="ml-2 text-muted-foreground">{data.total}</span> : null}
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
          <div className="px-4 py-6 text-center text-muted-foreground">No entries</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">URL</TableHead>
                <TableHead className="text-right">Visits</TableHead>
                <TableHead>First seen</TableHead>
                <TableHead>Last seen</TableHead>
                <TableHead>Registered</TableHead>
                <TableHead className="px-4" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((entry) => (
                <TableRow key={entry.instanceUrl}>
                  <TableCell className="px-4 font-mono">{entry.instanceUrl}</TableCell>
                  <TableCell className="text-right">{entry.visits}</TableCell>
                  <TableCell>{when(entry.firstSeenAt)}</TableCell>
                  <TableCell>{when(entry.lastSeenAt)}</TableCell>
                  <TableCell>{entry.alsoRegistered ? 'Yes' : 'No'}</TableCell>
                  <TableCell className="px-4 text-right">
                    <DeleteButton
                      onConfirm={async () => {
                        await api.deleteRegisterPageInstance(entry.instanceUrl)
                        reload()
                      }}
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </>
  )
}
