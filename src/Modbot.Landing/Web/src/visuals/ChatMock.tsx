import { SendHorizontal } from 'lucide-react'

/**
 * A Chat conversation as src/Modbot.Web/src/pages/Chat.tsx draws one: the question, the tools the
 * answer used (with the labels the server gives them, src/Modbot.Api/Features/Chat), the people they
 * found, and the answer. Sample words; the tool names are the real ones.
 */
export function ChatMock() {
  return (
    <div className="app flex flex-col overflow-hidden rounded-xl border bg-card text-card-foreground shadow-[0_30px_80px_-40px_rgb(22_24_31/0.35)]">
      <div className="flex items-center justify-between gap-3 border-b px-5 py-3">
        <div className="font-semibold tracking-tight" style={{ fontSize: '0.9375rem' }}>
          Chat
        </div>
        <span className="rounded-md border px-2.5 py-1 font-medium" style={{ fontSize: 'var(--text-small)' }}>
          New conversation
        </span>
      </div>

      <div className="flex flex-col gap-4 p-5" style={{ fontSize: '0.875rem' }}>
        <div className="ml-auto max-w-[85%] rounded-xl bg-secondary px-3.5 py-2.5">
          Has TeaSpoon been in trouble here before? They just joined Lantern Harbor.
        </div>

        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <Chip muted>Find a person</Chip>
            <Chip link>TeaSpoon</Chip>
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            <Chip muted>Person&rsquo;s history</Chip>
            <Chip muted>Person&rsquo;s bans</Chip>
          </div>
        </div>

        <div className="leading-relaxed">
          <p>
            <strong className="font-semibold">TeaSpoon</strong> has no bans, but two moderation actions on record, both
            on Sunday:
          </p>
          <ul className="mt-2 list-disc pl-5">
            <li>Kicked from room 48213 by Oto.</li>
            <li>Warned in Pixel Karaoke Hall by Wren.</li>
          </ul>
          <p className="mt-2">They left the group on Friday, so they are not a member now.</p>
        </div>
      </div>

      <div className="mt-auto flex items-center gap-2 border-t p-3">
        <div className="flex-1 rounded-md border bg-background px-3 py-2 text-muted-foreground" style={{ fontSize: '0.875rem' }}>
          Ask about your group
        </div>
        <span aria-hidden="true" className="grid size-9 place-items-center rounded-md bg-primary text-primary-foreground">
          <SendHorizontal className="size-4" />
        </span>
      </div>
    </div>
  )
}

function Chip({ children, muted, link }: { children: React.ReactNode; muted?: boolean; link?: boolean }) {
  return (
    <span
      className={
        link
          ? 'rounded-full border border-primary/40 bg-accent px-2.5 py-0.5 font-medium text-accent-foreground'
          : muted
            ? 'rounded-full border bg-background px-2.5 py-0.5 text-muted-foreground'
            : 'rounded-full border px-2.5 py-0.5'
      }
      style={{ fontSize: 'var(--text-small)' }}
    >
      {children}
    </span>
  )
}
