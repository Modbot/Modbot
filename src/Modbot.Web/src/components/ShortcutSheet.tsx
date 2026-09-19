import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'
import { useModal, useShortcutList, type Shortcut, type ShortcutGroup } from '@/lib/shortcuts'

const ORDER: ShortcutGroup[] = ['General', 'Go to', 'Lists', 'Filters', 'Popups']

/**
 * Everything the screen that is open can do, grouped, with the key for each. Opened with `?`, and
 * from the bar at the foot of a phone.
 *
 * Read from the registry rather than from a fixed table, so a key a page does not register is
 * not promised here.
 *
 * **Every row runs.** A phone has no keyboard, so a list of keys would be a list of things a
 * moderator on a phone cannot do — and these are not spare conveniences: `o` on the audit log
 * opens the person a row is about, and nothing else on that screen does. Each row is the control,
 * and the key beside it is how to reach the same control with a keyboard.
 */
export function ShortcutSheet({
  open,
  onOpenChange,
  title = 'Keyboard shortcuts',
  omit,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title?: string
  /** Groups to leave out. The bar at the foot of a phone drops "Go to", which is the Menu's job. */
  omit?: readonly ShortcutGroup[]
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {open && <Sheet title={title} omit={omit} onDone={() => onOpenChange(false)} />}
    </Dialog>
  )
}

function Sheet({
  title,
  omit,
  onDone,
}: {
  title: string
  omit?: readonly ShortcutGroup[]
  onDone: () => void
}) {
  useModal()
  const all = useShortcutList()

  // One row per key: the last registration is the one that fires, so it is the one listed.
  const byKeys = new Map<string, Shortcut>()
  for (const s of all) if (!s.hidden) byKeys.set(s.keys, s)

  const groups = ORDER.filter((group) => !omit?.includes(group))
    .map((group) => ({
      group,
      items: [...byKeys.values()].filter((s) => s.group === group),
    }))
    .filter((g) => g.items.length > 0)

  // Closed before it runs, the same order the command palette uses: a shortcut that opens a popup
  // or moves the list underneath should not have to fight this sheet for the screen.
  const run = (shortcut: Shortcut) => {
    onDone()
    shortcut.run(new KeyboardEvent('keydown'))
  }

  return (
    <DialogContent title={title} className="max-w-2xl" aria-describedby={undefined}>
      <div className="grid gap-x-8 gap-y-4 sm:grid-cols-2" style={{ fontSize: 'var(--text-small)' }}>
        {groups.map(({ group, items }) => (
          <section key={group} className="flex flex-col gap-1">
            <div className="font-medium">{group}</div>
            {items.map((s) => (
              <button
                key={s.keys}
                type="button"
                onClick={() => run(s)}
                className="-mx-2 flex items-center justify-between gap-3 rounded-md px-2 text-left hover:bg-secondary"
                style={{ minHeight: 'calc(var(--control-h) - 6px)' }}
              >
                <span className="text-muted-foreground">{s.label}</span>
                <Kbd keys={s.keys} className="shrink-0" />
              </button>
            ))}
          </section>
        ))}
      </div>
    </DialogContent>
  )
}
