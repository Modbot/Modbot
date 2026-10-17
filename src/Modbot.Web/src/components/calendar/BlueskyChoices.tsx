import { useState, type ReactNode } from 'react'
import { Check, Globe } from 'lucide-react'
import { Popover } from 'radix-ui'
import {
  BLUESKY_LANGUAGES,
  BLUESKY_LANGUAGES_MAX,
  REPLY_GROUPS,
  interactLabel,
  languagesLabel,
  replyGroupsOf,
  replyKind,
  withLanguageToggled,
  withReplyGroupToggled,
} from '@/lib/blueskyPostSettings'
import { PopoverPortal } from '@/components/ui/dialog-portal'

// The two pop-ups of Bluesky's New post box, in the box's own dim colours.
const PANEL = '#1c2733'
const LINE = '#2e4052'
const TEXT = '#ffffff'
const MUTED = '#aebbc9'
const LINK = '#208bfe'

/** One row of a pop-up: a tick or a dot at the left, the words after it. */
function Row({ checked, role, onClick, children }: { checked: boolean; role: 'radio' | 'checkbox'; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      role={role}
      aria-checked={checked}
      onClick={onClick}
      className="flex w-full items-center gap-2.5 rounded-md px-2 py-1.5 text-left text-[14px] hover:bg-white/5"
      style={{ color: TEXT }}
    >
      <span
        className={`flex size-5 shrink-0 items-center justify-center border ${role === 'radio' ? 'rounded-full' : 'rounded'}`}
        style={{ borderColor: checked ? LINK : MUTED, background: checked ? LINK : 'transparent' }}
      >
        {checked && <Check className="size-3.5" strokeWidth={3} />}
      </span>
      {children}
    </button>
  )
}

/** The chip under the post: what it says, and a pop-up with who can reply and whether quote posts are allowed. */
export function InteractChip({
  whoCanReply,
  quotesOff,
  onWhoCanReply,
  onQuotesOff,
}: {
  whoCanReply: string[]
  quotesOff: boolean
  onWhoCanReply: (choice: string[]) => void
  onQuotesOff: (off: boolean) => void
}) {
  const kind = replyKind(whoCanReply)
  const groups = replyGroupsOf(whoCanReply)

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          className="inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-[13px]"
          style={{ background: 'rgba(174,187,201,0.1)', color: MUTED }}
        >
          <Globe className="size-3.5" /> {interactLabel(whoCanReply, quotesOff)}
        </button>
      </Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="start"
          sideOffset={4}
          className="z-50 w-[300px] rounded-xl border p-2 shadow-lg"
          style={{ background: PANEL, borderColor: LINE, color: TEXT }}
        >
          <div role="radiogroup" aria-label="Who can reply" className="flex flex-col">
            <Row role="radio" checked={kind === 'anyone'} onClick={() => onWhoCanReply(['anyone'])}>
              Anyone
            </Row>
            <Row role="radio" checked={kind === 'nobody'} onClick={() => onWhoCanReply(['nobody'])}>
              Nobody
            </Row>
          </div>
          <div className="my-1 border-t" style={{ borderColor: LINE }} />
          <div role="group" aria-label="Some people" className="flex flex-col">
            {REPLY_GROUPS.map((g) => (
              <Row key={g.id} role="checkbox" checked={groups.includes(g.id)} onClick={() => onWhoCanReply(withReplyGroupToggled(whoCanReply, g.id))}>
                {g.label}
              </Row>
            ))}
          </div>
          <div className="my-1 border-t" style={{ borderColor: LINE }} />
          <Row role="checkbox" checked={!quotesOff} onClick={() => onQuotesOff(!quotesOff)}>
            Allow quote posts
          </Row>
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}

/** The language button at the bottom of the box, and a pop-up to pick up to three languages. */
export function LanguageButton({ languages, onLanguages }: { languages: string[]; onLanguages: (codes: string[]) => void }) {
  const [open, setOpen] = useState(false)
  const full = languages.length >= BLUESKY_LANGUAGES_MAX

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button type="button" aria-label="Languages" className="max-w-[200px] truncate text-[13px]" style={{ color: MUTED }}>
          {languagesLabel(languages)}
        </button>
      </Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="end"
          sideOffset={4}
          className="z-50 flex max-h-[320px] w-[240px] flex-col overflow-y-auto rounded-xl border p-2 shadow-lg"
          style={{ background: PANEL, borderColor: LINE, color: TEXT }}
        >
          {BLUESKY_LANGUAGES.map((l) => {
            const picked = languages.includes(l.code)
            return (
              <div key={l.code} className={full && !picked ? 'opacity-40' : ''}>
                <Row role="checkbox" checked={picked} onClick={() => onLanguages(withLanguageToggled(languages, l.code))}>
                  {l.name}
                </Row>
              </div>
            )
          })}
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}
