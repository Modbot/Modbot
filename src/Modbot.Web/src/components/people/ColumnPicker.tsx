import { Popover } from 'radix-ui'
import { Columns3 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/settings/fields'
import { withColumn, type PeopleColumn } from '@/lib/peopleColumns'
import { PopoverPortal } from '@/components/ui/dialog-portal'

/**
 * The Columns button: a list of the view's columns, each with a switch, and Reset to put the view's
 * own back. The person column always shows and is not in it. On a phone the list's width gives way
 * to the screen's, and the popover keeps off the edges.
 */
export function ColumnPicker({
  columns,
  hidden,
  onChange,
}: {
  columns: PeopleColumn[]
  hidden: readonly string[]
  onChange: (hidden: string[]) => void
}) {
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <Button variant="outline">
          <Columns3 className="size-3.5" />
          Columns
        </Button>
      </Popover.Trigger>
      <PopoverPortal>
        <Popover.Content align="start" sideOffset={4} collisionPadding={8} className="z-50 outline-none">
          <div
            className="w-64 max-w-[calc(100vw-1rem)] overflow-hidden rounded-sm border border-(length:--hairline) bg-popover text-popover-foreground shadow-sm"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <div className="flex flex-col px-2 py-1">
              {columns.map((column) => (
                <Switch
                  key={column.id}
                  checked={!hidden.includes(column.id)}
                  onChange={(on) => onChange(withColumn(hidden, column.id, on))}
                >
                  {column.label}
                </Switch>
              ))}
            </div>
            <div className="border-t border-t-(length:--hairline) px-2 py-1">
              <Button variant="ghost" disabled={hidden.length === 0} onClick={() => onChange([])}>
                Reset
              </Button>
            </div>
          </div>
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}
