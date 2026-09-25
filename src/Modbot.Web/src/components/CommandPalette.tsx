import { useEffect, useMemo, useRef, useState } from 'react'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { Search, X } from 'lucide-react'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { EmptyRow } from '@/components/PanelGrid'
import { Kbd } from '@/components/ui/kbd'
import { api, type CurrentUser, type SearchResults } from '@/lib/api'
import { NAV, mayOpen, type PageId } from '@/lib/nav'
import { useModal, useShortcutList } from '@/lib/shortcuts'
import { openDiscordPerson, openPerson, openWorld } from '@/lib/subject'
import { cn } from '@/lib/utils'

/** Something the palette can run that is not a page or a search hit: a theme, a density, sign out. */
export type PaletteAction = { id: string; label: string; group: string; keys?: string; run: () => void }

type Item = {
  id: string
  group: string
  label: string
  detail?: string
  keys?: string
  picture?: string | null
  run: () => void
}

/**
 * The command palette, on `Ctrl/Cmd+K`: pages, the keys that work on this screen, and a search
 * over people, Discord people and worlds.
 *
 * Typing narrows the pages and actions by name at once; two characters or more also asks the
 * server. `↑`/`↓` move, `Enter` runs, `Esc` closes. Opening a person, a world or a Discord person
 * from here goes through the same popup every list uses.
 */
export function CommandPalette({
  open,
  onOpenChange,
  me,
  onGoTo,
  actions,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  me: CurrentUser
  onGoTo: (page: PageId) => void
  actions: PaletteAction[]
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      {open && <Palette me={me} onGoTo={onGoTo} actions={actions} close={() => onOpenChange(false)} />}
    </DialogPrimitive.Root>
  )
}

function Palette({
  me,
  onGoTo,
  actions,
  close,
}: {
  me: CurrentUser
  onGoTo: (page: PageId) => void
  actions: PaletteAction[]
  close: () => void
}) {
  useModal()

  const [typed, setTyped] = useState('')
  const shortcuts = useShortcutList()
  const listRef = useRef<HTMLDivElement>(null)

  const query = typed.trim()

  // The answer is kept with the question it answered, so a stale answer is never shown against a
  // newer query and nothing has to be cleared when the query changes.
  const [answered, setAnswered] = useState<{ query: string; results: SearchResults } | null>(null)
  const results = answered?.query === query ? answered.results : null

  useEffect(() => {
    if (query.length < 2) return

    let cancelled = false
    const timer = setTimeout(() => {
      api
        .search(query)
        .then((next) => {
          if (!cancelled) setAnswered({ query, results: next })
        })
        .catch(() => {
          // Nothing found is what the list shows; a failed search reads the same.
        })
    }, 150)

    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [query])

  const goTo = useMemo(() => new Map(shortcuts.filter((s) => s.group === 'Go to').map((s) => [s.label, s.keys])), [shortcuts])

  const items = useMemo<Item[]>(() => {
    const pages: Item[] = NAV.filter((n) => !('hidden' in n && n.hidden) && mayOpen(me, n.id)).map((n) => ({
      id: `page:${n.id}`,
      group: 'Go to',
      label: n.label,
      keys: goTo.get(n.label),
      run: () => onGoTo(n.id),
    }))

    const keys: Item[] = shortcuts
      .filter((s) => !s.hidden && s.group !== 'Go to' && s.keys !== 'mod+k')
      .map((s) => ({
        id: `key:${s.keys}`,
        group: 'On this page',
        label: s.label,
        keys: s.keys,
        run: () => s.run(new KeyboardEvent('keydown')),
      }))

    const extra: Item[] = actions.map((a) => ({ id: `action:${a.id}`, group: a.group, label: a.label, keys: a.keys, run: a.run }))

    const found: Item[] = results
      ? [
          ...results.people.map((p) => ({
            id: `person:${p.userId}`,
            group: 'People',
            label: p.displayName ?? p.userId,
            detail: p.displayName ? p.userId : undefined,
            picture: p.avatarUrl,
            run: () => openPerson(p.userId),
          })),
          ...results.discordPeople.map((p) => ({
            id: `discord:${p.userId}`,
            group: 'Discord people',
            label: p.displayName,
            detail: `@${p.username}${p.inServer ? '' : ' · left'}`,
            picture: p.avatarUrl,
            run: () => openDiscordPerson(p.userId),
          })),
          ...results.worlds.map((w) => ({
            id: `world:${w.worldId}`,
            group: 'Worlds',
            label: w.name ?? w.worldId,
            detail: w.name ? w.worldId : undefined,
            picture: w.thumbnailImageUrl,
            run: () => openWorld(w.worldId),
          })),
        ]
      : []

    const words = query.toLowerCase().split(/\s+/).filter(Boolean)
    const matches = (item: Item) =>
      words.every((w) => item.label.toLowerCase().includes(w) || item.group.toLowerCase().includes(w))

    // Search hits are already narrowed by the server; the rest is narrowed by what was typed.
    return [...found, ...[...pages, ...keys, ...extra].filter(matches)]
  }, [me, goTo, shortcuts, actions, results, query, onGoTo])

  // The cursor belongs to one list: when the items change under it, it starts again at the top.
  const itemsKey = items.map((i) => i.id).join('\n')
  const [cursorState, setCursorState] = useState({ key: itemsKey, index: 0 })
  const cursor = cursorState.key === itemsKey ? cursorState.index : 0
  const setCursor = (update: number | ((c: number) => number)) =>
    setCursorState({ key: itemsKey, index: typeof update === 'function' ? update(cursor) : update })

  useEffect(() => {
    listRef.current?.querySelector(`[data-index="${cursor}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [cursor])

  const run = (item: Item) => {
    close()
    item.run()
  }

  const groups: { group: string; items: { item: Item; index: number }[] }[] = []
  items.forEach((item, index) => {
    const last = groups[groups.length - 1]
    if (last && last.group === item.group) last.items.push({ item, index })
    else groups.push({ group: item.group, items: [{ item, index }] })
  })

  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-foreground/30 dark:bg-background/70" />
      <DialogPrimitive.Content
        aria-describedby={undefined}
        className="fixed top-[12vh] left-1/2 z-50 flex max-h-[70vh] w-[calc(100vw-2rem)] max-w-xl -translate-x-1/2 flex-col overflow-hidden rounded-sm border border-(length:--hairline) bg-card text-card-foreground shadow-sm outline-none"
      >
        <DialogPrimitive.Title className="sr-only">Search and commands</DialogPrimitive.Title>

        <div className="flex items-center gap-2 border-b border-b-(length:--hairline) px-3">
          <Search className="size-4 shrink-0 text-muted-foreground" />
          <input
            autoFocus
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') {
                e.preventDefault()
                setCursor((c) => Math.min(items.length - 1, c + 1))
              } else if (e.key === 'ArrowUp') {
                e.preventDefault()
                setCursor((c) => Math.max(0, c - 1))
              } else if (e.key === 'Enter') {
                e.preventDefault()
                const item = items[cursor]
                if (item) run(item)
              }
            }}
            placeholder="Search people, worlds, pages and actions"
            aria-label="Search people, worlds, pages and actions"
            role="combobox"
            aria-expanded
            aria-controls="palette-list"
            aria-activedescendant={items[cursor] ? `palette-${items[cursor].id}` : undefined}
            className="h-[calc(var(--control-h)+var(--panel-pad))] w-full bg-transparent outline-none placeholder:text-muted-foreground"
          />
          <Kbd keys="escape" />
          {/* Escape is the way out on a keyboard; this is the way out without one. */}
          <button
            type="button"
            onClick={close}
            aria-label="Close"
            className="grid shrink-0 place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground lg:hidden"
            style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
          >
            <X className="size-4" />
          </button>
        </div>

        <div ref={listRef} id="palette-list" role="listbox" className="min-h-0 flex-1 overflow-auto py-1">
          {items.length === 0 && <EmptyRow className="px-3">Nothing matches</EmptyRow>}

          {groups.map(({ group, items: rows }) => (
            <div key={group}>
              <div
                className="flex items-center gap-2 px-3 pt-2 pb-1 font-label text-muted-foreground"
                style={{ fontSize: 'var(--text-small)' }}
              >
                {group}
                <span aria-hidden className="h-(--hairline) flex-1 bg-border" />
              </div>
              {rows.map(({ item, index }) => (
                <button
                  key={item.id}
                  type="button"
                  id={`palette-${item.id}`}
                  role="option"
                  aria-selected={index === cursor}
                  data-index={index}
                  onMouseEnter={() => setCursor(index)}
                  onClick={() => run(item)}
                  className={cn(
                    'flex w-full items-center gap-2 px-3 text-left',
                    index === cursor ? 'bg-accent text-accent-foreground' : 'hover:bg-muted',
                  )}
                  style={{ height: 'var(--row-h)' }}
                >
                  {item.picture !== undefined && <Avatar url={item.picture} className="size-6" />}
                  <span className="min-w-0 flex-1 truncate">{item.label}</span>
                  {item.detail && (
                    <span className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                      {item.detail}
                    </span>
                  )}
                  {item.keys && <Kbd keys={item.keys} />}
                </button>
              ))}
            </div>
          ))}
        </div>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  )
}
