import { useRef, useState, type KeyboardEvent } from 'react'
import { MoreHorizontal, Pencil, Plus, Search, Trash2 } from 'lucide-react'
import { Popover } from 'radix-ui'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import type { ChatConversationSummary } from '@/lib/api'
import { cn } from '@/lib/utils'

/** The headings a conversation list is broken into, newest first. */
const GROUPS = ['Today', 'Yesterday', 'Previous 7 days', 'Older'] as const

type Group = (typeof GROUPS)[number]

export function Conversations({
  conversations,
  currentId,
  busy,
  onNew,
  onOpen,
  onRename,
  onDelete,
}: {
  conversations: ChatConversationSummary[]
  currentId: string | null
  busy: boolean
  onNew: () => void
  onOpen: (id: string) => void
  onRename: (id: string, title: string) => void
  onDelete: (id: string) => void
}) {
  const [query, setQuery] = useState('')
  const [renaming, setRenaming] = useState<string | null>(null)
  const [deleting, setDeleting] = useState<ChatConversationSummary | null>(null)
  const list = useRef<HTMLDivElement | null>(null)

  const needle = query.trim().toLowerCase()
  const shown = needle ? conversations.filter((c) => c.title.toLowerCase().includes(needle)) : conversations

  const groups = GROUPS.map((group) => ({ group, items: shown.filter((c) => groupOf(c.updatedAt) === group) })).filter(
    (g) => g.items.length > 0,
  )

  // Up and down move through the list without leaving the keyboard.
  const move = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return

    const rows = [...(list.current?.querySelectorAll<HTMLButtonElement>('[data-conversation]') ?? [])]
    const at = rows.indexOf(document.activeElement as HTMLButtonElement)
    if (at < 0) return

    e.preventDefault()
    rows[Math.min(rows.length - 1, Math.max(0, at + (e.key === 'ArrowDown' ? 1 : -1)))]?.focus()
  }

  return (
    <div className="flex h-full min-h-0 flex-col gap-2">
      <Button size="sm" variant="outline" disabled={busy} onClick={onNew} className="justify-start">
        <Plus className="size-4" />
        New chat
      </Button>

      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input
          aria-label="Search conversations"
          placeholder="Search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="pl-7"
        />
      </div>

      <div
        ref={list}
        onKeyDown={move}
        className="-mr-1 flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pr-1"
        style={{ fontSize: 'var(--text-small)' }}
      >
        {groups.map(({ group, items }) => (
          <div key={group} className="flex flex-col gap-0.5">
            <h3 className="px-2 py-1 font-medium tracking-wide text-muted-foreground uppercase">{group}</h3>
            {items.map((c) => (
              <Row
                key={c.id}
                conversation={c}
                current={c.id === currentId}
                busy={busy}
                renaming={renaming === c.id}
                onOpen={() => onOpen(c.id)}
                onStartRename={() => setRenaming(c.id)}
                onRename={(title) => {
                  setRenaming(null)
                  if (title && title !== c.title) onRename(c.id, title)
                }}
                onDelete={() => setDeleting(c)}
              />
            ))}
          </div>
        ))}
      </div>

      <Dialog open={deleting !== null} onOpenChange={(open) => !open && setDeleting(null)}>
        <DialogContent title="Delete this conversation?" subtitle={deleting?.title} className="max-w-[380px]">
          <div className="flex justify-end gap-2">
            <Button size="sm" variant="outline" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button
              size="sm"
              variant="destructive"
              onClick={() => {
                if (deleting) onDelete(deleting.id)
                setDeleting(null)
              }}
            >
              Delete
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

function Row({
  conversation,
  current,
  busy,
  renaming,
  onOpen,
  onStartRename,
  onRename,
  onDelete,
}: {
  conversation: ChatConversationSummary
  current: boolean
  busy: boolean
  renaming: boolean
  onOpen: () => void
  onStartRename: () => void
  onRename: (title: string) => void
  onDelete: () => void
}) {
  const [menu, setMenu] = useState(false)

  if (renaming) return <RenameBox title={conversation.title} onDone={onRename} />

  return (
    <div className="group/row relative flex items-center">
      <button
        type="button"
        data-conversation
        disabled={busy}
        onClick={onOpen}
        aria-current={current ? 'true' : undefined}
        title={conversation.title}
        className={cn(
          'w-full truncate rounded-md py-1.5 pr-8 pl-2 text-left transition-colors',
          current
            ? 'bg-accent text-accent-foreground'
            : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground',
        )}
      >
        {conversation.title}
      </button>

      <Popover.Root open={menu} onOpenChange={setMenu}>
        <Popover.Trigger asChild>
          <button
            type="button"
            aria-label="More"
            className={cn(
              'absolute right-1 rounded-md p-1 text-muted-foreground opacity-0 transition hover:bg-secondary hover:text-foreground',
              'focus-visible:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100',
              menu && 'opacity-100',
            )}
          >
            <MoreHorizontal className="size-3.5" />
          </button>
        </Popover.Trigger>
        <Popover.Portal>
          <Popover.Content
            align="end"
            sideOffset={4}
            className="z-50 flex w-40 flex-col rounded-lg border bg-popover p-1 text-popover-foreground shadow-md"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <MenuItem
              onSelect={() => {
                setMenu(false)
                onStartRename()
              }}
            >
              <Pencil className="size-3.5" />
              Rename
            </MenuItem>
            <MenuItem
              destructive
              onSelect={() => {
                setMenu(false)
                onDelete()
              }}
            >
              <Trash2 className="size-3.5" />
              Delete
            </MenuItem>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
    </div>
  )
}

/** The name being typed. Mounted when renaming starts, so it opens on the name that is there. */
function RenameBox({ title, onDone }: { title: string; onDone: (title: string) => void }) {
  const [draft, setDraft] = useState(title)

  return (
    <Input
      autoFocus
      aria-label="Name"
      value={draft}
      maxLength={80}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => onDone(draft.trim())}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onDone(draft.trim())
        if (e.key === 'Escape') onDone('')
      }}
    />
  )
}

function MenuItem({
  children,
  destructive,
  onSelect,
}: {
  children: React.ReactNode
  destructive?: boolean
  onSelect: () => void
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      className={cn(
        'flex items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-accent hover:text-accent-foreground',
        destructive && 'text-destructive',
      )}
    >
      {children}
    </button>
  )
}

/** Which heading a conversation sits under, by the browser's own day. */
function groupOf(iso: string): Group {
  const day = new Date(iso)
  day.setHours(0, 0, 0, 0)

  const today = new Date()
  today.setHours(0, 0, 0, 0)

  const days = Math.round((today.getTime() - day.getTime()) / 86_400_000)

  if (days <= 0) return 'Today'
  if (days === 1) return 'Yesterday'
  if (days <= 7) return 'Previous 7 days'
  return 'Older'
}
