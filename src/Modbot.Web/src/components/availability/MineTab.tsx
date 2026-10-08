import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { ClockSwitch, Labelled, ZoneSelect } from '@/components/availability/Parts'
import { cellAt, type GridCell } from '@/components/availability/cells'
import { beginPaintStroke } from '@/components/availability/paintStroke'
import { cellsBetween, strokeAction, type Tool } from '@/components/availability/strokes'
import { WeekGrid } from '@/components/availability/WeekGrid'
import { beginPress, type PointerPoint } from '@/components/calendar/pointer'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ApiError } from '@/lib/api'
import { availabilityApi, type MyAvailability } from '@/lib/availability'
import { useClock } from '@/lib/availabilityClock'
import {
  browserZone,
  DAY_NAMES,
  DAY_ORDER,
  HOURS_IN_WEEK,
  hourText,
  type AvailabilityCell,
  type AvailabilityState,
} from '@/lib/availabilityZones'
import { usePhoneLayout } from '@/lib/phoneLayout'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'

const ALL_DAYS = DAY_ORDER
const ALL_HOURS = Array.from({ length: 24 }, (_, hour) => hour)

/** Free is solid; if needed is striped, so the two differ by more than a colour. */
const IF_NEEDED_STRIPES = 'repeating-linear-gradient(135deg, var(--warn) 0 2px, transparent 2px 6px)'

function week(saved: MyAvailability): (AvailabilityState | null)[] {
  const cells: (AvailabilityState | null)[] = Array.from({ length: HOURS_IN_WEEK }, () => null)
  for (const cell of saved.cells) cells[cell.day * 24 + cell.hour] = cell.state
  return cells
}

function listed(cells: readonly (AvailabilityState | null)[]): AvailabilityCell[] {
  const out: AvailabilityCell[] = []
  cells.forEach((state, at) => {
    if (state) out.push({ day: Math.floor(at / 24), hour: at % 24, state })
  })
  return out
}

/**
 * A person's own week: which hours they are free, painted on the grid with a finger or a mouse,
 * in the zone they pick. Nothing is saved until Save is pressed. The hours are kept on the person's
 * own clock, so the grid is never converted here.
 *
 * A mouse paints by dragging. A finger always paints, from the moment it touches a cell, and the
 * cells do not scroll under it; the hour labels, the day headers and everything outside the grid
 * still scroll the page. Pressing a cell that already holds the chosen state clears it, and the
 * rest of that stroke clears too.
 */
export function MineTab() {
  const [saved, setSaved] = useState<MyAvailability | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [zone, setZone] = useState(browserZone)
  const [cells, setCells] = useState<(AvailabilityState | null)[]>(() => week({ timeZone: null, cells: [], savedAt: null }))
  const [tool, setTool] = useState<Tool>('free')
  const [changed, setChanged] = useState(false)
  const [saving, setSaving] = useState(false)
  const [outcome, setOutcome] = useState<{ tone: 'ok' | 'problem'; text: string } | null>(null)
  const flipped = usePhoneLayout()
  const [clock, setClock] = useClock()

  // The week as the grid holds it, read synchronously by a stroke that paints many cells in a frame.
  const held = useRef(cells)

  const load = useCallback(() => {
    availabilityApi
      .mine()
      .then((answer) => {
        const next = week(answer)
        held.current = next
        setCells(next)
        setZone(answer.timeZone ?? browserZone())
        setSaved(answer)
        setChanged(false)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load your availability.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!saved) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  const apply = (cell: GridCell, state: AvailabilityState | null) => {
    const at = cell.day * 24 + cell.hour
    if (held.current[at] === state) return

    const next = [...held.current]
    next[at] = state
    held.current = next
    setCells(next)
    setChanged(true)
    setOutcome(null)
  }

  // What pressing this cell does: paint the chosen state, or clear the cell when it already has it.
  const actionAt = (cell: GridCell): AvailabilityState | null => strokeAction(tool, held.current[cell.day * 24 + cell.hour])

  const press = (event: ReactPointerEvent, first: GridCell) => {
    const action = actionAt(first)

    // Every cell between one sample and the next, so a fast move does not leave gaps in the stroke.
    let last = first
    const paintTo = (at: PointerPoint) => {
      const cell = cellAt(at)
      if (!cell) return
      for (const between of cellsBetween(last, cell, flipped, ALL_DAYS)) apply(between, action)
      last = cell
    }

    if (event.pointerType === 'touch') {
      apply(first, action)
      beginPaintStroke(event, { onMove: paintTo })
      return
    }

    beginPress(event, {
      onTap: () => apply(first, action),
      onStart: () => apply(first, action),
      onMove: paintTo,
    })
  }

  const save = () => {
    setSaving(true)
    setOutcome(null)

    availabilityApi
      .save(zone, listed(held.current))
      .then((answer) => {
        setSaved(answer)
        setZone(answer.timeZone ?? zone)
        setChanged(false)
        setOutcome({ tone: 'ok', text: 'Saved' })
      })
      .catch((e: unknown) => setOutcome({ tone: 'problem', text: e instanceof ApiError ? e.message : 'Could not save.' }))
      .finally(() => setSaving(false))
  }

  const stateAt = (cell: GridCell) => cells[cell.day * 24 + cell.hour]

  return (
    <Card>
      <CardHeader>
        <CardTitle>Week</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Labelled label="Time zone">
            <ZoneSelect
              value={zone}
              onChange={(next) => {
                setZone(next)
                setChanged(true)
                setOutcome(null)
              }}
            />
          </Labelled>

          <Labelled label="Clock">
            <ClockSwitch clock={clock} onChange={setClock} />
          </Labelled>

          <Labelled label="Paint">
            <SwitchBank
              label="Paint"
              value={tool}
              onChange={setTool}
              options={[
                { value: 'free', label: 'Free', icon: <span aria-hidden className="size-3 bg-primary" /> },
                {
                  value: 'ifNeeded',
                  label: 'If needed',
                  icon: <span aria-hidden className="size-3 bg-warn/30" style={{ backgroundImage: IF_NEEDED_STRIPES }} />,
                },
                { value: 'erase', label: 'Erase', icon: <span aria-hidden className="size-3 border border-input bg-muted" /> },
              ]}
            />
          </Labelled>
        </div>

        <WeekGrid
          days={ALL_DAYS}
          hours={ALL_HOURS}
          flipped={flipped}
          clock={clock}
          lockTouch
          cellClass={(cell) =>
            cn(stateAt(cell) === 'free' ? 'bg-primary' : stateAt(cell) === 'ifNeeded' ? 'bg-warn/30' : 'bg-muted')
          }
          cellStyle={(cell) => (stateAt(cell) === 'ifNeeded' ? { backgroundImage: IF_NEEDED_STRIPES } : undefined)}
          cellLabel={(cell) => {
            const state = stateAt(cell)
            return `${DAY_NAMES[cell.day]} ${hourText(cell.hour, clock)}, ${state === 'free' ? 'free' : state === 'ifNeeded' ? 'free if needed' : 'not free'}`
          }}
          onPress={press}
          onKey={(cell) => apply(cell, actionAt(cell))}
        />

        <div className="flex flex-wrap items-center gap-3">
          <Button onClick={save} disabled={saving || (!changed && saved.savedAt !== null)}>
            Save
          </Button>
          {outcome ? (
            <Outcome tone={outcome.tone}>{outcome.text}</Outcome>
          ) : changed ? (
            <span className="text-muted-foreground">Not saved</span>
          ) : saved.savedAt ? (
            <span className="text-muted-foreground">Saved</span>
          ) : null}
        </div>
      </CardContent>
    </Card>
  )
}
