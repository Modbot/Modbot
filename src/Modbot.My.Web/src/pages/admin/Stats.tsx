import { Card } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api } from '@/lib/api'
import { useAdminLoad } from '@/lib/useLoad'

export function Stats() {
  const { data, error } = useAdminLoad(() => api.stats(), [])

  if (error && error.status !== 401) return <p className="text-destructive">{error.message}</p>
  if (!data) return <p className="text-muted-foreground">Loading</p>

  const tiles = [
    { label: 'Registered', value: data.registeredInstances },
    { label: 'Active in 30 days', value: data.activeLast30Days },
    { label: 'With analytics', value: data.withAnalytics },
    { label: 'Register page', value: data.registerPageInstances },
    { label: 'Register page only', value: data.registerPageOnly },
  ]

  const versions = Object.entries(data.byVersion).sort((a, b) => b[1] - a[1] || b[0].localeCompare(a[0]))

  return (
    <>
      <h1 className="text-lg font-display">Stats</h1>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        {tiles.map((tile) => (
          <Card key={tile.label} className="gap-1 px-4 py-4" data-testid="stat">
            <div className="text-muted-foreground">{tile.label}</div>
            <div className="text-2xl font-semibold">{tile.value}</div>
          </Card>
        ))}
      </div>

      <Card className="gap-0 py-0">
        <h2 className="border-b px-4 py-3 font-semibold">By version</h2>
        {versions.length === 0 ? (
          <div className="px-4 py-6 text-center text-muted-foreground">No versions</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="px-4">Version</TableHead>
                <TableHead className="px-4 text-right">Instances</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {versions.map(([version, count]) => (
                <TableRow key={version}>
                  <TableCell className="px-4 font-mono">{version}</TableCell>
                  <TableCell className="px-4 text-right">{count}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </>
  )
}
