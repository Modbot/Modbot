import { useCallback, useEffect, useMemo, useState } from 'react'
import { ClockSwitch, Labelled, ZoneSelect } from '@/components/availability/Parts'
import type { GridCell } from '@/components/availability/cells'
import { WeekGrid } from '@/components/availability/WeekGrid'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Chip } from '@/components/ui/chip'
import { Select } from '@/components/ui/select'
import { SwitchBank } from '@/components/ui/switch-bank'
import { ApiError } from '@/lib/api'
import { availabilityApi, type TeamAvailability } from '@/lib/availability'
import { useClock } from '@/lib/availabilityClock'
import {
  bestTimes,
  bestTimeText,
  NO_FILTERS,
  rolesOf,
  shadeOf,
  shownDays,
  shownHours,
  shownPeople,
  tally,
  type DaysShown,
  type HoursShown,
  type TeamFilters,
} from '@/lib/availabilityTeam'
import { browserZone, DAY_NAMES, hourText, inViewersWeek, viewerWeek, weekStartFor } from '@/lib/availabilityZones'
import { usePhoneLayout } from '@/lib/phoneLayout'
import { PageMessage } from '@/pages/analytics/shared'

/** How dark each hour is drawn, from nobody to most of the people shown. Theme colours, so it holds in light and dark. */
const SHADES = [
  'bg-muted',
  'bg-primary/20 text-foreground',
  'bg-primary/40 text-foreground',
  'bg-primary/70 text-primary-foreground',
  'bg-primary text-primary-foreground',
] as const

const DAY_CHOICES: { value: DaysShown; label: string }[] = [
  { value: 'every', label: 'Every day' },
  { value: 'weekdays', label: 'Weekdays' },
  { value: 'weekend', label: 'Weekend' },
]

const HOUR_CHOICES: { value: HoursShown; label: string }[] = [
  { value: 'all', label: 'All hours' },
  { value: 'daytime', label: 'Daytime' },
  { value: 'evening', label: 'Evening' },
  { value: 'night', label: 'Night' },
]

const LEAST_CHOICES = [
  { value: '1', label: 'Any' },
  { value: '2', label: 'At least 2' },
  { value: '3', label: 'At least 3' },
  { value: '4', label: 'At least 4' },
] as const

/**
 * The team's weeks together: a heatmap of how many are free in each hour, who they are, and the best
 * stretch of each day. The hours are shown in the viewer's chosen zone, the browser's to begin with;
 * each person's week is moved there for the real dates of this week (`inViewersWeek`).
 */
export function TeamTab() {
  const [data, setData] = useState<TeamAvailability | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [zone, setZone] = useState(browserZone)
  const [filters, setFilters] = useState<TeamFilters>(NO_FILTERS)
  const [picked, setPicked] = useState<GridCell | null>(null)
  const [hovered, setHovered] = useState<GridCell | null>(null)
  const flipped = usePhoneLayout()
  const [clock, setClock] = useClock()

  // The moment the week is worked out for: when the tab opened. A week that changes while it is open is not worth a timer.
  const [opened] = useState(() => Date.now())

  const load = useCallback(() => {
    availabilityApi
      .team()
      .then((answer) => {
        setData(answer)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the availability.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const people = useMemo(() => data?.people ?? [], [data])
  const roles = useMemo(() => rolesOf(people), [people])
  const shown = useMemo(() => shownPeople(people, filters), [people, filters])

  // The week the grid shows is the one this moment falls in, by the chosen zone's calendar.
  const week = useMemo(() => viewerWeek(weekStartFor(opened, zone), zone), [opened, zone])
  const weeks = useMemo(() => new Map(people.map((p) => [p.id, inViewersWeek(p.cells, p.timeZone, week)])), [people, week])
  const slots = useMemo(() => tally(shown, weeks, filters.ifNeeded), [shown, weeks, filters.ifNeeded])

  const days = useMemo(() => shownDays(filters), [filters])
  const hours = useMemo(() => shownHours(filters), [filters])
  const best = useMemo(() => bestTimes(slots, days, hours, filters.least), [slots, days, hours, filters.least])

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>
  if (people.length === 0) return <PageMessage>Nobody can enter availability yet.</PageMessage>

  const set = (change: Partial<TeamFilters>) => setFilters((f) => ({ ...f, ...change }))
  const slotOf = (cell: GridCell) => slots[cell.day * 24 + cell.hour]
  const open = hovered ?? picked
  const openSlot = open ? slotOf(open) : null

  return (
    <div className="flex flex-col gap-3">
      <Card>
        <CardHeader>
          <CardTitle>Filters</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Labelled label="Time zone">
            <ZoneSelect value={zone} onChange={setZone} />
          </Labelled>

          <Labelled label="Clock">
            <ClockSwitch clock={clock} onChange={setClock} />
          </Labelled>

          <Labelled label="Role">
            <Select
              value={filters.role ?? ''}
              onChange={(role) => set({ role: role === '' ? null : role })}
              aria-label="Role"
            >
              <option value="">All roles</option>
              {roles.map((role) => (
                <option key={role} value={role}>
                  {role}
                </option>
              ))}
            </Select>
          </Labelled>

          <Labelled label="Day">
            <SwitchBank label="Day" value={filters.days} onChange={(days) => set({ days })} options={DAY_CHOICES} />
          </Labelled>

          <Labelled label="Hours">
            <SwitchBank label="Hours" value={filters.hours} onChange={(hours) => set({ hours })} options={HOUR_CHOICES} />
          </Labelled>

          <Labelled label="Free at once">
            <SwitchBank
              label="Free at once"
              value={String(filters.least)}
              onChange={(least) => set({ least: Number(least) as TeamFilters['least'] })}
              options={[...LEAST_CHOICES]}
            />
          </Labelled>

          <Labelled label="If needed">
            <Checkbox checked={filters.ifNeeded} onChange={(ifNeeded) => set({ ifNeeded })}>
              Counts as free
            </Checkbox>
          </Labelled>

          <Labelled label="People" className="col-span-full">
            <div className="flex flex-wrap gap-1.5">
              {people.map((person) => (
                <Chip
                  key={person.id}
                  on={filters.people.includes(person.id)}
                  onClick={() =>
                    set({
                      people: filters.people.includes(person.id)
                        ? filters.people.filter((id) => id !== person.id)
                        : [...filters.people, person.id],
                    })
                  }
                >
                  {person.name}
                </Chip>
              ))}
            </div>
          </Labelled>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Heatmap</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <WeekGrid
            days={days}
            hours={hours}
            flipped={flipped}
            clock={clock}
            selected={picked}
            cellClass={(cell) => SHADES[shadeOf(slotOf(cell).count, shown.length, filters.least)]}
            cellContent={(cell) => (shadeOf(slotOf(cell).count, shown.length, filters.least) > 0 ? slotOf(cell).count : null)}
            cellLabel={(cell) => `${DAY_NAMES[cell.day]} ${hourText(cell.hour, clock)}, ${slotOf(cell).count} free`}
            onChoose={(cell) => setPicked((now) => (now?.day === cell.day && now.hour === cell.hour ? null : cell))}
            onHover={setHovered}
          />

          <div className="flex flex-col gap-1" aria-live="polite">
            <div className="font-label">{open ? `${DAY_NAMES[open.day]} ${hourText(open.hour, clock)}` : 'Hour'}</div>
            {!openSlot ? (
              <span className="text-muted-foreground">No hour picked.</span>
            ) : openSlot.people.length === 0 ? (
              <span className="text-muted-foreground">Nobody is free.</span>
            ) : (
              <ul className="flex flex-wrap gap-x-4 gap-y-1">
                {[...openSlot.people]
                  .sort((a, b) => a.name.localeCompare(b.name))
                  .map((person) => (
                    <li key={person.id}>
                      {person.name}
                      {person.state === 'ifNeeded' && <span className="text-muted-foreground"> (if needed)</span>}
                    </li>
                  ))}
              </ul>
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Best times</CardTitle>
        </CardHeader>
        <CardContent>
          {best.length === 0 ? (
            <span className="text-muted-foreground">Nobody is free.</span>
          ) : (
            <ul className="flex flex-col gap-1">
              {best.map((time) => (
                <li key={time.day}>{bestTimeText(time, shown.length, clock)}</li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
