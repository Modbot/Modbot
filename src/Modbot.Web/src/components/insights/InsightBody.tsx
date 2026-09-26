import { ChevronRight } from 'lucide-react'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import type { Insight } from '@/lib/api'
import { insightDays } from './days'

const number = (n: number | null) => (n === null ? '—' : n.toLocaleString())

/**
 * What the model wrote, with the figures it was given a click away.
 *
 * The figures sit under the text rather than on another screen because they are how a reader
 * checks the text: the model is told to use only them, and nothing makes it.
 */
export function InsightBody({ insight }: { insight: Insight }) {
  const figures = insight.figures

  return (
    <div className="flex flex-col gap-2">
      <p className="whitespace-pre-wrap" style={{ fontSize: 'var(--text-small)' }}>
        {insight.text}
      </p>

      {figures && (
        <details className="group">
          <summary
            className="flex w-fit cursor-pointer list-none items-center gap-1 rounded-sm text-muted-foreground select-none hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <ChevronRight
              className="size-3.5 shrink-0 transition-transform group-open:rotate-90 motion-reduce:transition-none"
              aria-hidden
            />
            Figures
          </summary>
          <div className="mt-2 flex flex-col gap-3">
            <Table
              head={
                <>
                  <Th />
                  <Th className="text-right font-mono">{insightDays(figures)}</Th>
                  <Th className="text-right font-mono">
                    {insightDays({ firstDay: figures.beforeFirstDay, lastDay: figures.beforeLastDay })}
                  </Th>
                </>
              }
            >
              {figures.figures.map((f) => (
                <Tr key={f.name}>
                  <Td>{f.name}</Td>
                  <Td className="text-right font-mono">{number(f.now)}</Td>
                  <Td className="text-right font-mono">{number(f.before)}</Td>
                </Tr>
              ))}
            </Table>

            {figures.lists
              .filter((l) => l.items.length > 0)
              .map((l) => (
                <Table
                  key={l.name}
                  head={
                    <>
                      <Th>{l.name}</Th>
                      <Th />
                    </>
                  }
                >
                  {l.items.map((item, i) => (
                    <Tr key={`${i}-${item.name}`}>
                      <Td>{item.name}</Td>
                      <Td className="text-right font-mono">{number(item.value)}</Td>
                    </Tr>
                  ))}
                </Table>
              ))}
          </div>
        </details>
      )}
    </div>
  )
}
