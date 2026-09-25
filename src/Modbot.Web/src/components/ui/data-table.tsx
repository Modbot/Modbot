import { cn } from '@/lib/utils'

/**
 * A table run to its panel's edges: the column names on the strip, one hairline between rows.
 * `pinFirst` keeps the first column in place while a phone scrolls the rest sideways, for tables
 * whose first column names the row.
 */
export function Table({
  head,
  pinFirst = false,
  children,
}: {
  head: React.ReactNode
  pinFirst?: boolean
  children: React.ReactNode
}) {
  return (
    <div data-pin-first={pinFirst || undefined} className="relative overflow-x-auto">
      <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="bg-strip text-left text-muted-foreground">
          <tr>{head}</tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}

export function Th({ className, ...props }: React.ComponentProps<'th'>) {
  return <th className={cn('px-(--panel-pad) py-1.5 font-normal whitespace-nowrap', className)} {...props} />
}

export function Tr({ className, ...props }: React.ComponentProps<'tr'>) {
  return <tr className={cn('h-(--row-h) border-t border-t-(length:--hairline)', className)} {...props} />
}

export function Td({ className, ...props }: React.ComponentProps<'td'>) {
  return <td className={cn('px-(--panel-pad) py-1 whitespace-nowrap', className)} {...props} />
}
