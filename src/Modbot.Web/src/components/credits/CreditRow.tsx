import { Code2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'

export type Credit = {
  name: string
  url: string | null
  licence: string | null
  version?: string
  source?: string | null
  usedBy?: string[]
  note?: string | null
}

/**
 * One credit. On a wide screen: name, version, licence in three aligned columns. On a narrow one
 * the three simply wrap, so a long package name gets the whole width rather than a sliver of it.
 */
export function CreditRow({ item }: { item: Credit }) {
  return (
    <li
      className="flex flex-wrap items-center gap-x-3 gap-y-0.5 border-b border-b-(length:--hairline) px-(--panel-pad) py-1.5 last:border-b-0 sm:grid sm:grid-cols-[minmax(0,1fr)_minmax(0,12rem)_auto]"
      style={{ minHeight: 'var(--row-h)' }}
    >
      <div className="flex min-w-0 flex-[1_1_100%] flex-wrap items-center gap-x-2 gap-y-1 sm:flex-auto">
        {item.url ? (
          <a href={item.url} target="_blank" rel="noreferrer" className="font-medium [overflow-wrap:anywhere] hover:underline">
            {item.name}
          </a>
        ) : (
          <span className="font-medium [overflow-wrap:anywhere]">{item.name}</span>
        )}
        {item.source && (
          <a
            href={item.source}
            target="_blank"
            rel="noreferrer"
            title="Source"
            aria-label={`${item.name} source`}
            className="text-muted-foreground hover:text-foreground"
          >
            <Code2 className="size-3.5" />
          </a>
        )}
        {item.usedBy?.map((part) => (
          <Badge key={part} variant="outline" className="font-normal">
            {part}
          </Badge>
        ))}
      </div>

      <span
        className="min-w-0 truncate font-mono text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
        title={item.version}
      >
        {item.version}
      </span>

      <div className="ml-auto sm:justify-self-end">
        {item.licence && (
          <Badge variant="secondary" className="font-mono font-normal">
            {item.licence}
          </Badge>
        )}
      </div>

      {item.note && (
        <p className="basis-full text-muted-foreground sm:col-span-full" style={{ fontSize: 'var(--text-small)' }}>
          {item.note}
        </p>
      )}
    </li>
  )
}

/** A card of credits with a heading and a count, used by the Libraries and Services tabs. */
export function CreditCard({
  title,
  groups,
}: {
  title?: string
  groups: { label?: string; items: Credit[] }[]
}) {
  return (
    <Card>
      {title && (
        <CardHeader>
          <CardTitle>
            <h2>{title}</h2>
          </CardTitle>
          <CardAction className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {groups.reduce((n, g) => n + g.items.length, 0)}
          </CardAction>
        </CardHeader>
      )}

      {groups.map((g, i) => (
        <div key={g.label ?? i} className={i > 0 ? 'border-t' : undefined} style={{ borderTopWidth: 'var(--hairline)' }}>
          {g.label && (
            <h3
              className="flex items-center border-b bg-strip px-(--panel-pad) text-muted-foreground"
              style={{ borderBottomWidth: 'var(--hairline)', minHeight: 'var(--strip-h)', fontSize: 'var(--text-small)' }}
            >
              {g.label}
            </h3>
          )}
          <ul>
            {g.items.map((item) => (
              <CreditRow key={`${item.name} ${item.version ?? ''}`} item={item} />
            ))}
          </ul>
        </div>
      ))}
    </Card>
  )
}
