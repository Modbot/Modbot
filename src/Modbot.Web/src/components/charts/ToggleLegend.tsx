import { LegendSwatch, type LegendSample } from './RankedList'

/**
 * One key in a legend whose lines can be turned on and off. An item with `on` set is a control
 * and draws as a button; one without is a plain key, for a line that is always there.
 */
export type ToggleLegendItem = {
  key: string
  label: string
  color: string
  sample?: LegendSample
  on?: boolean
}

/**
 * A legend that is also the chart's controls: click a key to draw or hide its line. A hidden
 * line's key stays, muted, so the chart still says which lines it could show. The plain keys
 * sit first, then the toggles, in the order given.
 */
export function ToggleLegend({ items, onToggle }: { items: ToggleLegendItem[]; onToggle: (key: string) => void }) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      {items.map((item) =>
        item.on === undefined ? (
          <span key={item.key} className="flex items-center gap-1.5 px-1 text-muted-foreground">
            <LegendSwatch sample={item.sample} color={item.color} />
            {item.label}
          </span>
        ) : (
          <button
            key={item.key}
            type="button"
            aria-pressed={item.on}
            onClick={() => onToggle(item.key)}
            className={
              'flex items-center gap-1.5 rounded-sm px-1 hover:bg-accent phone:min-h-(--control-h) ' +
              (item.on ? 'text-foreground' : 'text-muted-foreground opacity-60')
            }
          >
            <LegendSwatch sample={item.sample} color={item.color} />
            {item.label}
          </button>
        ),
      )}
    </div>
  )
}
