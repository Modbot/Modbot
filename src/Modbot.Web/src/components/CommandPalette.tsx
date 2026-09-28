import { useEffect, useMemo, useRef, useState } from 'react'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { Search, X } from 'lucide-react'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Kbd } from '@/components/ui/kbd'
import { api, type CurrentUser, type SearchResults } from '@/lib/api'
import { NAV, goesByName, matchRank, mayOpen, otherWords, type PageId } from '@/lib/nav'
import { mayDo, splitVerb, verbLabel } from '@/lib/paletteActions'
import { useModal, useShortcutList } from '@/lib/shortcuts'
import { openDiscordPerson, openPerson, openSubject, openWorld } from '@/lib/subject'
import { cn } from '@/lib/utils'

type Standing = 'Member' | 'Left' | 'Banned'

/** The People page's badges for the same three answers, so a person looks the same in both. */
const STANDING_LOOK: Record<Standing, 'secondary' | 'outline' | 'destructive'> = {
  Member: 'secondary',
  Left: 'outline',
  Banned: 'destructive',
}

/** Where a person found stands with the group, as the People page's Standing column says it. */
function standingOf(p: SearchResults['people'][number]): Standing[] {
  const standing: Standing[] = []
  if (p.isMember) standing.push('Member')
  else if (p.left) standing.push('Left')
  if (p.banned) standing.push('Banned')
  return standing
}

/** Something the palette can run that is not a page or a search hit: a theme, a density, sign out. */
export type PaletteAction = { id: string; label: string; group: string; keys?: string; run: () => void }

type Item = {
  id: string
  group: string
  label: string
  detail?: string
  standing?: Standing[]
  keys?: string
  picture?: string | null
  run: () => void
}

/**
 * The command palette, on `Ctrl/Cmd+K`: pages, the keys that work on this screen, and a search
 * over people, Discord people and worlds.
 *
 * Typing narrows the pages and actions by name at once, and pages by the other words they answer
 * to (`words` in lib/nav.ts); two characters or more also asks the server. `↑`/`↓` move, `Enter`
 * runs, `Esc` closes. Opening a person, a world or a Discord person from here goes through the
 * same popup every list uses.
 *
 * Ban, kick, unban or note followed by a name ("ban fenya") offers that action on each person
 * found, where the popup's buttons would offer it; the row opens the popup with the confirmation,
 * or the Notes tab, already open (lib/paletteActions.ts).
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

  // "ban fenya" searches for fenya and offers the ban; the pages are still matched on all of it.
  const verbed = splitVerb(query)
  const verb = verbed?.verb ?? null
  const asked = verbed ? verbed.name : query

  // The answer is kept with the question it answered, so a stale answer is never shown against a
  // newer query and nothing has to be cleared when the query changes.
  // A failed search is kept the same way, so "Nothing matches" is never said about a search that
  // did not happen.
  const [answered, setAnswered] = useState<{ query: string; results: SearchResults | null } | null>(null)
  const results = answered?.query === asked ? answered.results : null
  const searchFailed = asked.length >= 2 && answered?.query === asked && answered.results === null

  useEffect(() => {
    if (asked.length < 2) return

    let cancelled = false
    const timer = setTimeout(() => {
      api
        .search(asked)
        .then((next) => {
          if (!cancelled) setAnswered({ query: asked, results: next })
        })
        .catch(() => {
          if (!cancelled) setAnswered({ query: asked, results: null })
        })
    }, 150)

    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [asked])

  const goTo = useMemo(() => new Map(shortcuts.filter((s) => s.group === 'Go to').map((s) => [s.label, s.keys])), [shortcuts])

  const items = useMemo<Item[]>(() => {
    const pages: (Item & { other: readonly string[] })[] = NAV.filter((n) => goesByName(n) && mayOpen(me, n.id)).map((n) => ({
      id: `page:${n.id}`,
      group: 'Go to',
      label: n.label,
      other: otherWords(n),
      keys: goTo.get(n.label),
      run: () => onGoTo(n.id),
    }))

    const keys: Item[] = shortcuts
      .filter((s) => !s.hidden && s.group !== 'Go to' && s.keys !== 'mod+k')
      .map((s) => ({
        id: `key:${s.keys ?? `${s.group}:${s.label}`}`,
        // A sort order is a name alone ("By name"), so it keeps its heading.
        group: s.group === 'Sort' ? 'Sort' : 'On this page',
        label: s.label,
        keys: s.keys,
        run: () => s.run(new KeyboardEvent('keydown')),
      }))

    const extra: Item[] = actions.map((a) => ({ id: `action:${a.id}`, group: a.group, label: a.label, keys: a.keys, run: a.run }))

    // Where each person stands, in the People page's words, rather than their id: the id told a
    // moderator nothing, and "is this the member or the one we already banned" is the question.
    // The id still finds them; it is only not printed.
    const found: Item[] = results
      ? [
          ...results.people.map((p) => ({
            id: `person:${p.userId}`,
            group: 'People',
            label: p.displayName ?? p.userId,
            standing: standingOf(p),
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

    // "ban fenya": the ban, for each person found whom this moderator may ban, by the popup's own
    // rule. The row opens the popup with the confirmation already open; nothing is sent from here.
    const acts: Item[] =
      verb && results
        ? [
            ...results.people
              .filter((p) => mayDo(me, verb, p))
              .map((p) => ({
                id: `act:${verb}:${p.userId}`,
                group: 'Actions',
                label: verbLabel(verb, p.displayName ?? p.userId),
                picture: p.avatarUrl,
                run: () =>
                  verb === 'note'
                    ? openSubject({ kind: 'person', id: p.userId }, { tab: 'notes' })
                    : openSubject({ kind: 'person', id: p.userId }, { act: verb }),
              })),
            // A note can be about somebody Modbot only knows from Discord; the others are VRChat's.
            ...(verb === 'note'
              ? results.discordPeople
                  .filter((p) => mayDo(me, 'note', { userId: p.userId }))
                  .map((p) => ({
                    id: `act:note:discord:${p.userId}`,
                    group: 'Actions',
                    label: verbLabel('note', p.displayName),
                    picture: p.avatarUrl,
                    run: () => openSubject({ kind: 'discord-person', id: p.userId }, { tab: 'notes' }),
                  }))
              : []),
          ]
        : []

    // Search hits are already narrowed by the server; the rest is narrowed by what was typed. A page
    // that matches comes before them: "banned" is the Bans page before it is a person called
    // BannedPrincess. Pages are ordered by how well they match, the rest keep their order so each
    // group stays in one piece.
    const ranked = pages
      .map((item) => ({ item, rank: matchRank(query, item.label, [...item.other, item.group]) }))
      .filter((r) => r.rank !== null)
      .sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0))
      .map((r) => r.item)
    const rest = [...keys, ...extra].filter((item) => matchRank(query, item.label, [item.group]) !== null)

    return [...ranked, ...acts, ...found, ...rest]
  }, [me, goTo, shortcuts, actions, results, query, verb, onGoTo])

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
          {searchFailed && (
            <EmptyRow tone="danger" className="px-3">
              Could not search
            </EmptyRow>
          )}
          {items.length === 0 && !searchFailed && <EmptyRow className="px-3">Nothing matches</EmptyRow>}

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
                  {item.standing?.map((s) => (
                    <Badge key={s} variant={STANDING_LOOK[s]}>
                      {s}
                    </Badge>
                  ))}
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
