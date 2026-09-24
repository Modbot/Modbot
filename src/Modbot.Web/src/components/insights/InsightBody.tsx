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
        <details className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <summary className="w-fit cursor-pointer select-none">Figures</summary>
          <div className="relative mt-2 overflow-x-auto">
            <table className="w-full">
              <thead className="text-left">
                <tr>
                  <th className="py-1 font-normal" />
                  <th className="py-1 text-right font-normal">{insightDays(figures)}</th>
                  <th className="py-1 text-right font-normal">
                    {insightDays({ firstDay: figures.beforeFirstDay, lastDay: figures.beforeLastDay })}
                  </th>
                </tr>
              </thead>
              <tbody className="text-foreground">
                {figures.figures.map((f) => (
                  <tr key={f.name} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                    <td className="py-1 pr-3">{f.name}</td>
                    <td className="py-1 text-right font-mono">{number(f.now)}</td>
                    <td className="py-1 text-right font-mono">{number(f.before)}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            {figures.lists
              .filter((l) => l.items.length > 0)
              .map((l) => (
                <table key={l.name} className="mt-3 w-full">
                  <thead className="text-left">
                    <tr>
                      <th className="py-1 font-normal">{l.name}</th>
                      <th className="py-1" />
                    </tr>
                  </thead>
                  <tbody className="text-foreground">
                    {l.items.map((item, i) => (
                      <tr key={`${i}-${item.name}`} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                        <td className="py-1 pr-3">{item.name}</td>
                        <td className="py-1 text-right font-mono">{number(item.value)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              ))}
          </div>
        </details>
      )}
    </div>
  )
}
