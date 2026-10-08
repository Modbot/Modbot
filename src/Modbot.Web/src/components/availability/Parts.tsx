import { useMemo, type ReactNode } from 'react'
import { Select } from '@/components/ui/select'
import { SwitchBank } from '@/components/ui/switch-bank'
import { zoneChoices, type ClockFormat } from '@/lib/availabilityZones'

/** A control with its name above it. */
export function Labelled({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  return (
    <div className={`flex min-w-0 flex-col gap-1 ${className ?? ''}`}>
      <span className="font-label text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </span>
      {children}
    </div>
  )
}

const CLOCK_CHOICES: { value: ClockFormat; label: string }[] = [
  { value: '24h', label: '24h' },
  { value: '12h', label: '12h' },
]

/** Whether hours are written 17:00 or 5 PM. The choice is shared by both tabs (`useClock`). */
export function ClockSwitch({ clock, onChange }: { clock: ClockFormat; onChange: (clock: ClockFormat) => void }) {
  return <SwitchBank label="Clock" value={clock} onChange={onChange} options={CLOCK_CHOICES} />
}

/** Every time zone the browser knows, and the one in use if the list leaves it out. */
export function ZoneSelect({ value, onChange, label = 'Time zone' }: { value: string; onChange: (zone: string) => void; label?: string }) {
  const zones = useMemo(() => zoneChoices([value]), [value])

  return (
    <Select value={value} onChange={onChange} aria-label={label}>
      {zones.map((zone) => (
        <option key={zone} value={zone}>
          {zone}
        </option>
      ))}
    </Select>
  )
}
