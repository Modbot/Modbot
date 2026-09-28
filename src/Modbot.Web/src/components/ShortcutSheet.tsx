import { Check } from 'lucide-react'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'
import { isListedKey, isPageAction, useModal, useShortcutList, type Shortcut, type ShortcutGroup } from '@/lib/shortcuts'
import { cn } from '@/lib/utils'

const ORDER: ShortcutGroup[] = ['General', 'Go to', 'Page', 'Lists', 'Filters', 'Sort', 'Calendar', 'Popups']

/**
 * Everything the screen that is open can do, grouped. Opened with `?` as the list of keys, and from
 * the bar at the foot of a phone as Actions.
 *
 * Read from the registry rather than from a fixed table, so a key a page does not register is
 * not promised here.
 *
 * **Every row runs.** Each row is the control, and the key beside it is how to reach the same
 * control with a keyboard. As Actions it leaves out the keys that only mean something at a
 * keyboard: it once held nothing on a list page but "Next row" and "Previous row", which a finger or
 * a laser pointer has no use for (review 2026-09-27, idea 8).
 */
export function ShortcutSheet({
  open,
  onOpenChange,
  actions = false,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /**
   * The Actions sheet the bar at the foot of a phone opens, rather than the `?` list of keys: the
   * page's own actions, with a key or without, and none of the app's or the keyboard's own.
   */
  actions?: boolean
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {open && <Sheet actions={actions} onDone={() => onOpenChange(false)} />}
    </Dialog>
  )
}

function Sheet({ actions, onDone }: { actions: boolean; onDone: () => void }) {
  useModal()
  const all = useShortcutList()

  // One row per key: the last registration is the one that fires, so it is the one listed. An
  // action with no key is one row per name.
  const byKeys = new Map<string, Shortcut>()
  for (const s of all) if (actions ? isPageAction(s) : isListedKey(s)) byKeys.set(s.keys ?? `${s.group}:${s.label}`, s)

  const groups = ORDER.map((group) => ({ group, items: [...byKeys.values()].filter((s) => s.group === group) })).filter(
    (g) => g.items.length > 0,
  )

  // Closed before it runs, the same order the command palette uses: a shortcut that opens a popup
  // or moves the list underneath should not have to fight this sheet for the screen.
  const run = (shortcut: Shortcut) => {
    onDone()
    shortcut.run(new KeyboardEvent('keydown'))
  }

  return (
    <DialogContent title={actions ? 'Actions' : 'Keyboard shortcuts'} className="max-w-2xl" aria-describedby={undefined}>
      <div className="grid gap-x-8 gap-y-4 sm:grid-cols-2" style={{ fontSize: 'var(--text-small)' }}>
        {groups.map(({ group, items }) => (
          <section key={group} className="flex flex-col">
            <div className="flex items-center gap-2 pb-1 font-label text-muted-foreground">
              {group}
              <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
            </div>
            {items.map((s) => (
              <button
                key={s.keys ?? s.label}
                type="button"
                onClick={() => run(s)}
                aria-pressed={s.checked}
                className="-mx-2 flex items-center justify-between gap-3 px-2 text-left hover:bg-muted"
                style={{ minHeight: 'var(--control-h)' }}
              >
                <span className={cn('flex items-center gap-2', s.checked ? 'text-foreground' : 'text-muted-foreground')}>
                  {/* A set of choices, such as the sort orders, keeps its labels in line whichever is ticked. */}
                  {s.checked !== undefined && <Check aria-hidden className={cn('size-4 shrink-0', !s.checked && 'invisible')} />}
                  {s.label}
                </span>
                {s.keys && <Kbd keys={s.keys} className="shrink-0" />}
              </button>
            ))}
          </section>
        ))}
      </div>
    </DialogContent>
  )
}
