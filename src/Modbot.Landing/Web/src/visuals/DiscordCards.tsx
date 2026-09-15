import { ExternalLink, Hash } from 'lucide-react'

/**
 * What the bot posts, with the fields and colours src/Modbot.Discord writes
 * (Instances/InstanceCard.cs, ModerationLog/ModerationEventEmbed.cs). Drawn as a generic chat
 * channel, not as Discord's own interface.
 */
export function DiscordCards() {
  return (
    <div className="overflow-hidden rounded-xl border bg-card shadow-[0_30px_80px_-40px_rgb(22_24_31/0.35)]">
      <div className="flex items-center gap-2 border-b px-4 py-2.5 text-sm font-medium">
        <Hash className="size-4 text-muted-foreground" aria-hidden="true" />
        instances
      </div>

      <div className="flex flex-col gap-6 p-4 sm:p-5">
        <Message time="Today at 21:00">
          <Embed colour="#3BA55D">
            <div className="flex flex-col gap-3">
              <span className="font-semibold text-info underline-offset-2">Lantern Harbor</span>
              <Fields
                fields={[
                  ['People here now', '6 people'],
                  ['Most at once', '9'],
                  ['Open for', '1h 45m'],
                  ['Who can join', 'Members and friends'],
                  ['Region', 'US'],
                  ['Capacity', '40'],
                  ['Instance', '48213'],
                ]}
              />
              <div className="text-sm">
                <div className="font-semibold">Who is here</div>
                <div className="text-muted-foreground">Juniper.exe, Kiri, mossfox, NovaDrift, Oto, pebble</div>
              </div>
              <div
                aria-hidden="true"
                className="aspect-[16/5] w-full rounded-md"
                style={{ background: 'radial-gradient(120% 90% at 20% 15%, #f6a45c, transparent 60%), linear-gradient(160deg, #5b4bd6, #16181f)' }}
              />
              <div className="text-xs text-muted-foreground">Open now</div>
            </div>
          </Embed>
          <span className="mt-2 inline-flex h-8 items-center gap-1.5 rounded-md bg-secondary px-3 text-sm font-medium">
            Join
            <ExternalLink className="size-3.5" aria-hidden="true" />
          </span>
        </Message>

      </div>

      <div className="flex items-center gap-2 border-y px-4 py-2.5 text-sm font-medium">
        <Hash className="size-4 text-muted-foreground" aria-hidden="true" />
        mod-log
      </div>

      <div className="flex flex-col gap-6 p-4 sm:p-5">
        <Message time="Today at 22:51">
          <Embed colour="#ED4245">
            <div className="flex flex-col gap-3">
              <span className="font-semibold">Banned</span>
              <div className="border-l-2 pl-2 text-sm text-muted-foreground">Crashing or malicious avatars</div>
              <Fields
                fields={[
                  ['Who', 'TeaSpoon'],
                  ['By', 'Wren'],
                  ['When', 'Today at 22:50'],
                ]}
              />
            </div>
          </Embed>
        </Message>
      </div>
    </div>
  )
}

function Message({ time, children }: { time: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-3">
      <span aria-hidden="true" className="grid size-9 shrink-0 place-items-center overflow-hidden rounded-full bg-accent">
        <img src="/icon-512.png" alt="" width={26} height={26} className="size-[1.625rem]" />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-semibold">Modbot</span>
          <span className="rounded bg-primary px-1 text-[10px] font-semibold text-primary-foreground">bot</span>
          <span className="text-xs text-muted-foreground">{time}</span>
        </div>
        <div className="mt-1.5">{children}</div>
      </div>
    </div>
  )
}

function Embed({ colour, children }: { colour: string; children: React.ReactNode }) {
  return (
    <div className="max-w-[30rem] rounded-md border-l-4 bg-secondary p-3 sm:p-4" style={{ borderLeftColor: colour }}>
      {children}
    </div>
  )
}

function Fields({ fields }: { fields: [string, string][] }) {
  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
      {fields.map(([name, value]) => (
        <div key={name} className="min-w-0">
          <dt className="font-semibold">{name}</dt>
          <dd className="break-words text-muted-foreground">{value}</dd>
        </div>
      ))}
    </dl>
  )
}
