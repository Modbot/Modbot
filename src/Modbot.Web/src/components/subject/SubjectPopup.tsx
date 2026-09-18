import { ArrowLeft } from 'lucide-react'
import { Dialog } from '@/components/ui/dialog'
import { InstancePopup } from '@/components/subject/InstancePopup'
import { PersonPopup } from '@/components/subject/PersonPopup'
import { WorldPopup } from '@/components/subject/WorldPopup'
import type { CurrentUser } from '@/lib/api'
import { useModal } from '@/lib/shortcuts'
import { closeSubject, encodeSubject, isPerson, useSubjects, type Subject } from '@/lib/subject'

/**
 * The popup: a person, a world or an instance, over whatever page is open.
 *
 * Spec 10.2's reason for a side pane rather than a page still decides everything here. Moderation
 * is interruption-driven — somebody scanning the audit log notices a name and needs to look
 * without losing the scan. So the popup sits over the page and never replaces it: the page behind
 * stays mounted, with its scroll position, its filters and its loaded rows, and closing the popup
 * puts the moderator exactly back where they were.
 *
 * What changed is the shape. A centre popup rather than a right-hand pane, because a person, a
 * world and an instance each want their identity on the left and several tabs on the right, and a
 * narrow pane had room for one column. And several kinds rather than one, because every one of
 * them names the others.
 *
 * Opening from inside a popup stacks (see `lib/subject.ts` for how the stack is written into the
 * URL). Only the top of the stack is drawn; the one underneath is named in the back control.
 *
 * Three of the five kinds are one popup. A VRChat account, a Discord account and a Modbot account
 * are three ways of naming the same human being, so all three open the person popup, which ties
 * them together from whichever one the link named (one view per person design §3). They stay
 * three values in the address because a link has to say which kind of id it carries.
 */
export function SubjectPopup({ me }: { me: CurrentUser }) {
  const stack = useSubjects()
  const top = stack[stack.length - 1]

  if (!top) return null

  const below = stack.length > 1 ? stack[stack.length - 2] : null
  const lead = below ? <Back below={below} depth={stack.length} /> : null

  // Keyed on the depth as well as the subject, so the same world opened twice in one stack is two
  // popups, and returning to the one underneath loads it fresh rather than showing the top's data
  // under the lower one's name for a frame.
  const key = `${stack.length}:${encodeSubject(top)}`

  return (
    <Open key={key} top={top} me={me} lead={lead} />
  )
}

/** The popup while one is open. Its own component so the page's keys go quiet only while it is mounted. */
function Open({ top, me, lead }: { top: Subject; me: CurrentUser; lead: React.ReactNode }) {
  useModal()

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        // Escape, the close button and a click outside all close one level, never the whole
        // stack — the same as the browser's back button.
        if (!open) closeSubject()
      }}
    >
      {isPerson(top) && <PersonPopup subject={top} me={me} lead={lead} />}
      {top.kind === 'world' && <WorldPopup id={top.id} me={me} lead={lead} />}
      {top.kind === 'instance' && <InstancePopup id={top.id} me={me} lead={lead} />}
    </Dialog>
  )
}

// All three ways of naming a human being read as "the person": they open the same popup, so
// saying anything else in the back control would name a screen that does not exist.
const KIND_WORD: Record<Subject['kind'], string> = {
  person: 'person',
  world: 'world',
  instance: 'instance',
  'discord-person': 'person',
  account: 'person',
}

function Back({ below, depth }: { below: Subject; depth: number }) {
  return (
    <button
      type="button"
      onClick={() => closeSubject()}
      title={depth > 2 ? `${depth - 1} more open underneath` : undefined}
      className="flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <ArrowLeft className="size-4" />
      Back to the {KIND_WORD[below.kind]}
    </button>
  )
}
