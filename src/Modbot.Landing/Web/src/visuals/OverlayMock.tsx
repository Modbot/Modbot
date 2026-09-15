/**
 * The SteamVR overlay's two panels as src/Modbot.Overlay/Views/OverlayView.cs draws them: the alert
 * card with its thick red left edge, and the roster, flagged first, then staff, then members, with
 * how fresh it is always stated. Always dark, because it floats in a headset over whatever world
 * you are in.
 */
const ROSTER: { name: string; standing: 'Flagged' | 'Staff' | 'Member' | 'Ordinary'; flags?: string }[] = [
  { name: 'TeaSpoon', standing: 'Flagged', flags: '2 prior actions' },
  { name: 'Oto', standing: 'Staff' },
  { name: 'Juniper.exe', standing: 'Member' },
  { name: 'mossfox', standing: 'Member' },
  { name: 'pebble', standing: 'Member' },
  { name: 'Kiri', standing: 'Ordinary' },
]

const DOT = { Flagged: '#f0526a', Staff: '#b6acff', Member: '#3fbf8f', Ordinary: '#4a5261' }

const panel = { background: 'rgb(28 31 40 / 0.94)', border: '1px solid #363c4a' }

export function OverlayMock() {
  return (
    <div
      className="relative overflow-hidden rounded-xl border px-4 py-8 sm:p-10"
      style={{
        background:
          'radial-gradient(90% 70% at 15% 10%, rgb(246 164 92 / 0.55), transparent 60%), radial-gradient(80% 80% at 90% 90%, rgb(42 120 214 / 0.5), transparent 60%), linear-gradient(160deg, #2a2547, #0d0e12)',
      }}
    >
      <div className="mx-auto flex max-w-[26rem] flex-col gap-3 text-[#f2f4f8]">
        <div className="flex flex-col gap-1.5 rounded-xl px-5 py-4" style={{ ...panel, borderLeft: '6px solid #f0526a' }}>
          <div className="text-sm font-semibold text-[#b9c0cf]">Flagged user joined · Lantern Social</div>
          <div className="text-2xl font-semibold">TeaSpoon</div>
          <div>2 prior moderation actions</div>
          <div className="text-sm text-[#b9c0cf]">2 prior actions</div>
        </div>

        <div className="flex flex-col rounded-xl px-5 py-4" style={panel}>
          <div className="mb-1.5 flex justify-between gap-3 text-sm font-semibold text-[#b9c0cf]">
            <span>Lantern Social</span>
            <span className="font-normal">up to date</span>
          </div>
          {ROSTER.map((row) => (
            <div key={row.name} className="flex h-9 items-center gap-3">
              <span className="size-3 shrink-0 rounded-full" style={{ background: DOT[row.standing] }} />
              <span className={row.standing === 'Flagged' ? 'font-semibold' : 'text-[#b9c0cf]'}>{row.name}</span>
              {row.flags && <span className="text-sm text-[#f0526a]">{row.flags}</span>}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
