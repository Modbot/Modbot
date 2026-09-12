import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'

type Member = { name: string; id: string; role: string; joined: string; inWorld: string; actions: number }

const MEMBERS: Member[] = [
  { name: 'ΛƧƬΛ', id: 'usr_6d99027a-39f0-40f4-ba2d-ffdce1a4b881', role: 'Moderator, Events', joined: '14 Mar 2025', inWorld: '18h 22m', actions: 3 },
  { name: '-winter~', id: 'usr_527e5167-9361-49f7-8358-3c7be9f0a2c1', role: 'Member', joined: '02 Jan 2026', inWorld: '41h 08m', actions: 0 },
  { name: 'BlackIndium', id: 'usr_786ac3f2-8e1a-45b6-b8d1-6531f0c9ab47', role: 'Member', joined: '19 Nov 2025', inWorld: '6h 44m', actions: 0 },
  { name: 'Fraiinco2Wavy', id: 'usr_f5ca805a-9a90-40a7-89d4-6b2e1c7d5590', role: 'Trusted', joined: '07 Aug 2025', inWorld: '92h 15m', actions: 1 },
  { name: 'CODYYYYYYYYYYYY', id: 'usr_f42ff73b-3d4a-45c0-b549-8e0a2d1f3c66', role: 'Member', joined: '23 Feb 2026', inWorld: '2h 01m', actions: 0 },
  { name: '~ RedZu ~', id: 'usr_56f38b9d-713e-4adc-a83c-d49185f2c730', role: 'Moderator', joined: '30 Apr 2024', inWorld: '214h 52m', actions: 0 },
  { name: 'MAR2109HD', id: 'usr_8ac41e2b-55d7-4f19-9c3a-1b6e7d820f45', role: 'Member', joined: '18 Sep 2025', inWorld: '11h 03m', actions: 2 },
  { name: 'hevy1015', id: 'usr_c8043f7e-6d21-4a90-b5c7-2e94a1f6038d', role: 'Member', joined: '08 May 2025', inWorld: '74h 30m', actions: 0 },
]

const initials = (n: string) => (n.replace(/[^\p{L}\p{N}]/gu, '').slice(0, 2) || '··').toUpperCase()

export function Members() {
  return (
    <div className="space-y-3">
      <div className="flex gap-2">
        <Input placeholder="Search name or user id…" className="max-w-80" style={{ height: 'var(--control-h)' }} />
        <Button variant="outline" style={{ height: 'var(--control-h)' }}>Role: any</Button>
        <div className="flex-1" />
        <Button variant="outline" style={{ height: 'var(--control-h)' }}>Export</Button>
      </div>

      <Card className="overflow-hidden p-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Member</TableHead>
              <TableHead>Roles</TableHead>
              <TableHead>Joined</TableHead>
              <TableHead className="text-right">In world</TableHead>
              <TableHead className="text-right">Actions</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {MEMBERS.map((m) => (
              <TableRow key={m.id} className="group" style={{ height: 'var(--row-h)' }}>
                <TableCell>
                  <div className="flex items-center gap-2">
                    <div className="grid size-6 shrink-0 place-items-center rounded bg-secondary text-[0.625rem] font-semibold text-muted-foreground">
                      {initials(m.name)}
                    </div>
                    <div className="leading-tight">
                      <div className="font-medium">{m.name}</div>
                      <div className="font-mono text-muted-foreground/70" style={{ fontSize: 'var(--text-small)' }}>
                        {m.id.slice(0, 22)}…
                      </div>
                    </div>
                  </div>
                </TableCell>
                <TableCell className="text-muted-foreground">{m.role}</TableCell>
                <TableCell className="text-muted-foreground">{m.joined}</TableCell>
                <TableCell className="text-right font-mono">{m.inWorld}</TableCell>
                <TableCell className="text-right">
                  {m.actions > 0
                    ? <Badge variant="destructive">{m.actions}</Badge>
                    : <span className="text-muted-foreground/50">—</span>}
                </TableCell>
                <TableCell>
                  <div className="flex justify-end gap-1 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
                    <Button variant="outline" size="sm">Dossier</Button>
                    {/* Destructive sits behind a deliberate gap: separation first,
                        outline second, colour third. Red alone fails for
                        colour-blind moderators and stops registering with use. */}
                    <span className="w-5" aria-hidden />
                    <Button variant="outline" size="sm" className="border-destructive text-destructive hover:bg-destructive/10">
                      Ban
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>
    </div>
  )
}
