import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'
import { useModal, useShortcutList, type Shortcut, type ShortcutGroup } from '@/lib/shortcuts'

const ORDER: ShortcutGroup[] = ['General', 'Go to', 'Lists', 'Filters', 'Popups']

/**
 * Every key that works on the screen that is open, grouped. Opened with `?`.
 *
 * Read from the registry rather than from a fixed table, so a key a page does not register is
 * not promised here.
 */
export function ShortcutSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {open && <Sheet />}
    </Dialog>
  )
}

function Sheet() {
  useModal()
  const all = useShortcutList()

  // One row per key: the last registration is the one that fires, so it is the one listed.
  const byKeys = new Map<string, Shortcut>()
  for (const s of all) if (!s.hidden) byKeys.set(s.keys, s)

  const groups = ORDER.map((group) => ({
    group,
    items: [...byKeys.values()].filter((s) => s.group === group),
  })).filter((g) => g.items.length > 0)

  return (
    <DialogContent title="Keyboard shortcuts" className="max-w-2xl" aria-describedby={undefined}>
      <div className="grid gap-x-8 gap-y-4 sm:grid-cols-2" style={{ fontSize: 'var(--text-small)' }}>
        {groups.map(({ group, items }) => (
          <section key={group} className="flex flex-col gap-1">
            <div className="font-medium">{group}</div>
            {items.map((s) => (
              <div key={s.keys} className="flex items-center justify-between gap-3 py-0.5">
                <span className="text-muted-foreground">{s.label}</span>
                <Kbd keys={s.keys} />
              </div>
            ))}
          </section>
        ))}
      </div>
    </DialogContent>
  )
}
