import { FileVideo, Fingerprint } from 'lucide-react'

/** A case file, laid out in the sections src/Modbot.Web/src/pages/CaseFile.tsx uses. */
export function CaseFileMock() {
  return (
    <div className="app overflow-hidden rounded-xl border bg-card text-card-foreground shadow-[0_30px_80px_-40px_rgb(22_24_31/0.35)]">
      <div className="flex items-center gap-3 border-b px-5 py-3">
        <div className="min-w-0 flex-1">
          <div className="font-semibold tracking-tight" style={{ fontSize: '0.9375rem' }}>
            Case file: TeaSpoon
          </div>
          <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Banned 13 Sep 2026 by Wren
          </div>
        </div>
        <span className="rounded-full bg-secondary px-2 py-0.5 font-medium" style={{ fontSize: 'var(--text-small)' }}>
          Edited twice
        </span>
      </div>

      <div className="grid gap-5 p-5 sm:grid-cols-[minmax(0,1fr)_13rem]">
        <div className="flex flex-col gap-5">
          <section className="flex flex-col gap-2">
            <h3 className="font-medium">Why they were banned</h3>
            <div className="flex flex-wrap gap-1">
              <span className="rounded-full bg-primary px-2 py-0.5 font-medium text-primary-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Crashing or malicious avatars
              </span>
              <span className="rounded-full bg-primary px-2 py-0.5 font-medium text-primary-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Ban evasion
              </span>
            </div>
            <div className="rounded-md border bg-background px-3 py-2 leading-relaxed">
              Switched into an avatar that froze the room twice, about ten minutes apart. Warned after the first.
              Came back on a second account the next evening; the clip shows both.
            </div>
          </section>

          <section className="flex flex-col gap-2">
            <h3 className="font-medium">Evidence</h3>
            <div className="grid grid-cols-3 gap-2">
              {[
                ['#2a78d6', '#16181f'],
                ['#eb6834', '#2a2547'],
              ].map(([a, b], i) => (
                <div
                  key={i}
                  aria-hidden="true"
                  className="aspect-video rounded-md"
                  style={{ background: `radial-gradient(100% 80% at 30% 30%, ${a}, transparent 70%), ${b}` }}
                />
              ))}
              <div className="grid aspect-video place-items-center rounded-md bg-secondary text-muted-foreground">
                <FileVideo className="size-5" aria-hidden="true" />
              </div>
            </div>
            <div className="flex items-center gap-1.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <Fingerprint className="size-3.5 shrink-0" aria-hidden="true" />
              <span className="truncate font-mono">sha256 9f2c41e0b7d3a8…</span>
            </div>
          </section>
        </div>

        <section className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
          <h3 className="font-medium" style={{ fontSize: 'var(--text-base)' }}>
            The profile at the time
          </h3>
          <div className="flex flex-col gap-2 rounded-md border px-3 py-2">
            <Row label="Display name" value="TeaSpoon" />
            <Row label="Joined VRChat" value="Aug 2025" />
            <Row label="Last platform" value="Quest" />
            <Row label="Status" value="Ask me" />
          </div>
          <div className="rounded-md border px-3 py-2">
            <div className="font-medium">Membership at the time</div>
            <div className="text-muted-foreground">Not a member; left 11 Sep 2026.</div>
          </div>
        </section>
      </div>
    </div>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-3">
      <span className="text-muted-foreground">{label}</span>
      <span className="truncate font-medium">{value}</span>
    </div>
  )
}
