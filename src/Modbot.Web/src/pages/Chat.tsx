import { useCallback, useEffect, useRef, useState } from 'react'
import { ArrowDown, Coins, PanelLeft, PanelLeftClose, PenSquare } from 'lucide-react'
import { Popover } from 'radix-ui'
import { Composer } from '@/components/chat/Composer'
import { Conversations } from '@/components/chat/Conversations'
import { Thread } from '@/components/chat/Thread'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import {
  api,
  ApiError,
  type AiSpent,
  type ChatConversationSummary,
  type ChatMessage,
  type ChatStreamEvent,
} from '@/lib/api'
import { spentText, tokensText, tokensTitle } from '@/lib/aiSpend'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'

/** How far from the bottom still counts as "at the bottom" while a reply streams in. */
const NEAR_BOTTOM = 80

/** A reply is announced to a screen reader no more often than this, in milliseconds. */
const ANNOUNCE_EVERY = 2500

const MAX_MESSAGE_LENGTH = 4000

/** The conversation on screen: which one it is, what it says, and whether it takes any more. */
type Thread = { id: string | null; messages: ChatMessage[]; full: boolean }

/**
 * Chat -- questions answered by the configured model, from Modbot's own data (AI chat design).
 *
 * The reply streams in as it is written, with each lookup shown as a step that opens to what it
 * asked and what came back. Conversations are the signed-in person's own, live at their own
 * address, and keep every version of a question and of a reply.
 */
export function Chat({
  conversationId,
  onOpenConversation,
}: {
  /** From `/chat/:id`. Null on `/chat`, which is a new conversation. */
  conversationId: string | null
  onOpenConversation: (id: string | null, options?: { replace?: boolean }) => void
}) {
  const [available, setAvailable] = useState<boolean | null>(null)
  const [model, setModel] = useState<string | null>(null)
  const [conversations, setConversations] = useState<ChatConversationSummary[]>([])
  const [error, setError] = useState<string | null>(null)

  // Kept with the conversation it belongs to, so opening another one shows nothing of the last.
  const [thread, setThread] = useState<Thread>({ id: null, messages: [], full: false })

  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [streamed, setStreamed] = useState('')
  /** What a stopped reply had written, until the stored copy of it is read back. */
  const [kept, setKept] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [limit, setLimit] = useState<string | null>(null)

  const [sidebar, setSidebar] = useState(true)
  const [drawer, setDrawer] = useState(false)
  const [pinned, setPinned] = useState(true)
  const [announced, setAnnounced] = useState('')

  const abort = useRef<AbortController | null>(null)
  const scroller = useRef<HTMLDivElement | null>(null)
  const bottom = useRef<HTMLDivElement | null>(null)
  const composer = useRef<HTMLTextAreaElement | null>(null)
  const announcedAt = useRef(0)
  const written = useRef('')

  const current = conversationId
  const messages = thread.id === current ? thread.messages : []
  const full = thread.id === current && thread.full

  // A conversation the page has not got yet is a conversation being read.
  const loading = current !== null && thread.id !== current

  const loadHome = useCallback(
    () =>
      api
        .chatHome()
        .then((home) => {
          setAvailable(home.available)
          setModel(home.model)
          setConversations(home.conversations)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to use Chat.'
              : 'Could not load Chat.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void loadHome()
    return () => abort.current?.abort()
  }, [loadHome])

  // Opening a conversation, including by pasting its address. A conversation this page is already
  // holding -- the one a reply is streaming into, above all -- is not read again.
  useEffect(() => {
    if (!current || thread.id === current) return

    let left = false

    api
      .chatConversation(current)
      .then((c) => {
        if (!left) setThread({ id: c.id, messages: c.messages, full: c.full })
      })
      .catch(() => {
        if (left) return

        // Claimed even though it could not be read, or this would ask for it again every render.
        setThread({ id: current, messages: [], full: false })
        setProblem('Could not load this conversation.')
      })

    return () => {
      left = true
    }
  }, [current, thread.id])

  useEffect(() => {
    composer.current?.focus()
  }, [current])

  // Stays at the bottom while a reply is written, unless the reader has scrolled up to something.
  useEffect(() => {
    if (pinned) bottom.current?.scrollIntoView({ block: 'end' })
  }, [thread.messages, streamed, pinned])

  // Slash focuses the message box from anywhere on the page.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return

      const on = e.target as HTMLElement | null
      if (on && (on.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(on.tagName))) return

      e.preventDefault()
      composer.current?.focus()
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  const openConversation = (id: string | null) => {
    if (busy) return
    setDrawer(false)
    setPinned(true)
    onOpenConversation(id)
  }

  const remove = (id: string) => {
    api
      .deleteChatConversation(id)
      .then(() => {
        setConversations((list) => list.filter((c) => c.id !== id))
        if (id === current) onOpenConversation(null)
      })
      .catch(() => setProblem('Could not delete this conversation.'))
  }

  const rename = (id: string, title: string) => {
    setConversations((list) => list.map((c) => (c.id === id ? { ...c, title } : c)))

    api.renameChatConversation(id, title).catch(() => {
      setProblem('Could not rename this conversation.')
      void loadHome()
    })
  }

  const readVersion = (messageId: number) => {
    if (!current || busy) return

    api
      .readChatVersion(current, messageId)
      .then((c) => {
        setThread({ id: c.id, messages: c.messages, full: c.full })
        setPinned(true)
      })
      .catch(() => setProblem('Could not open that version.'))
  }

  const send = (options: { text?: string; replaceMessageId?: number; retryAfterMessageId?: number }) => {
    const text = (options.text ?? '').trim()
    const branching = options.replaceMessageId !== undefined || options.retryAfterMessageId !== undefined

    if (busy) return
    if (!branching && !text) return

    setBusy(true)
    setProblem(null)
    setStreamed('')
    setKept('')
    setPinned(true)
    written.current = ''
    if (!branching) setDraft('')

    // A question asked again, or edited, is answered from where it hangs: what came after the old
    // one is not gone, it is just not what is being read any more.
    const cutAt =
      options.retryAfterMessageId !== undefined
        ? (list: ChatMessage[]) => indexOfId(list, options.retryAfterMessageId!) + 1
        : options.replaceMessageId !== undefined
          ? (list: ChatMessage[]) => Math.max(0, indexOfId(list, options.replaceMessageId!))
          : null

    if (cutAt) setThread((t) => ({ ...t, messages: t.messages.slice(0, cutAt(t.messages)) }))

    const controller = new AbortController()
    abort.current = controller

    const onEvent = (event: ChatStreamEvent) => {
      switch (event.type) {
        case 'conversation':
          setConversations((list) => [event.data, ...list.filter((c) => c.id !== event.data.id)])
          if (event.data.id !== current) {
            // Claimed here as well as in the address bar, so the reply streams into the thread
            // already on screen rather than into a conversation the page then reads back.
            setThread((t) => ({ ...t, id: event.data.id }))
            onOpenConversation(event.data.id, { replace: true })
          }
          break
        case 'message':
          setThread((t) => ({ ...t, messages: [...t.messages.filter((m) => m.id !== event.data.id), event.data] }))
          if (event.data.role === 'assistant') {
            setStreamed('')
            written.current = ''
          }
          break
        case 'text':
          written.current += event.data.text
          setStreamed((s) => s + event.data.text)
          break
        case 'done':
          if (event.data.error) setProblem(event.data.error)
          break
      }
    }

    api
      .sendChatMessage(
        {
          conversationId: current,
          text,
          replaceMessageId: options.replaceMessageId,
          retryAfterMessageId: options.retryAfterMessageId,
        },
        onEvent,
        controller.signal,
      )
      .catch((e: unknown) => {
        if (controller.signal.aborted) return

        const message = e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'
        setProblem(message)

        if (e instanceof ApiError && e.status === 429) setLimit(message)
        if (e instanceof ApiError && e.status === 409 && /full/i.test(message))
          setThread((t) => ({ ...t, full: true }))
        if (e instanceof ApiError && e.status !== 0 && !branching) setDraft(text)
      })
      .finally(() => {
        const stopped = controller.signal.aborted

        setBusy(false)
        setStreamed('')
        abort.current = null
        composer.current?.focus()

        // A stopped reply is kept on screen until the server's own copy of it arrives, which it
        // finishes storing a moment after the browser stops listening.
        if (stopped) setKept(written.current)

        // Reading it back is also how another version of a message gets its count: what the
        // others are versions of is only known to the server.
        if ((branching || stopped) && current) {
          window.setTimeout(
            () =>
              api
                .chatConversation(current)
                .then((c) => {
                  setThread({ id: c.id, messages: c.messages, full: c.full })
                  setKept('')
                })
                .catch(() => undefined),
            stopped ? 700 : 0,
          )
        }
      })
  }

  // Throttled, so a screen reader is not read a reply one word at a time.
  useEffect(() => {
    if (!streamed) return

    const since = Date.now() - announcedAt.current
    const wait = Math.max(0, ANNOUNCE_EVERY - since)

    const timer = window.setTimeout(() => {
      announcedAt.current = Date.now()
      setAnnounced(streamed)
    }, wait)

    return () => window.clearTimeout(timer)
  }, [streamed])

  if (error) return <PageMessage>{error}</PageMessage>
  if (available === null) return <PageMessage>Loading…</PageMessage>
  if (!available && conversations.length === 0) return <PageMessage>Chat is off.</PageMessage>

  const title = conversations.find((c) => c.id === current)?.title
  const empty = messages.length === 0 && !loading && !busy

  const disabledReason = !available
    ? 'Chat is off.'
    : full
      ? 'This conversation is full. Start a new one.'
      : limit

  const list = (
    <Conversations
      conversations={conversations}
      currentId={current}
      busy={busy}
      onNew={() => openConversation(null)}
      onOpen={openConversation}
      onRename={rename}
      onDelete={remove}
    />
  )

  return (
    // `dvh`, and more taken off it on a phone: the bar at the foot of the screen stands over the
    // page, and a `26rem` floor is taller than what is left on a small phone, which pushed the
    // box somebody types in off the bottom.
    <div className="flex h-[calc(100dvh-16rem)] min-h-[20rem] gap-4 lg:h-[calc(100dvh-11rem)] lg:min-h-[26rem]">
      {sidebar && <nav aria-label="Conversations" className="hidden w-60 shrink-0 lg:block">{list}</nav>}

      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-2 pb-2">
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={sidebar ? 'Hide conversations' : 'Show conversations'}
            onClick={() => setSidebar((s) => !s)}
            className="hidden lg:inline-flex"
          >
            {sidebar ? <PanelLeftClose className="size-4" /> : <PanelLeft className="size-4" />}
          </Button>
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label="Conversations"
            onClick={() => setDrawer(true)}
            className="lg:hidden"
          >
            <PanelLeft className="size-4" />
          </Button>

          <h2 className="min-w-0 flex-1 truncate font-medium">{current ? title : 'New chat'}</h2>

          {current && <Spend key={current} conversationId={current} />}

          <Button size="icon-sm" variant="ghost" aria-label="New chat" onClick={() => openConversation(null)}>
            <PenSquare className="size-4" />
          </Button>
        </div>

        {/* An empty conversation is the message box, in the middle, and nothing else. */}
        <div className={cn('relative flex min-h-0 flex-1 flex-col', empty && 'justify-end')}>
          <div
            ref={scroller}
            onScroll={(e) => {
              const box = e.currentTarget
              setPinned(box.scrollHeight - box.scrollTop - box.clientHeight < NEAR_BOTTOM)
            }}
            className={cn('flex min-h-0 flex-col overflow-y-auto', empty ? 'flex-none' : 'flex-1')}
          >
            <div className="mx-auto w-full max-w-3xl px-1 pb-4">
              {loading ? (
                <Skeleton />
              ) : (
                <Thread
                  messages={messages}
                  streamed={busy ? streamed : kept}
                  stopped={!busy && kept.length > 0}
                  writing={
                    (busy && (streamed.length > 0 || messages.at(-1)?.role !== 'assistant')) ||
                    (!busy && kept.length > 0)
                  }
                  busy={busy}
                  onRetry={(afterMessageId) => send({ retryAfterMessageId: afterMessageId })}
                  onEdit={(messageId, text) => send({ text, replaceMessageId: messageId })}
                  onReadVersion={readVersion}
                />
              )}
              <div ref={bottom} />
            </div>
          </div>

          {!pinned && (
            <button
              type="button"
              onClick={() => {
                setPinned(true)
                bottom.current?.scrollIntoView({ block: 'end', behavior: 'smooth' })
              }}
              className="absolute bottom-3 left-1/2 flex -translate-x-1/2 items-center gap-1.5 rounded-full border bg-card px-3 py-1.5 shadow-md hover:bg-accent hover:text-accent-foreground"
              style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
            >
              <ArrowDown className="size-3.5" />
              Jump to latest
            </button>
          )}
        </div>

        <div className={cn('mx-auto w-full max-w-3xl pt-2', empty && 'mb-[18vh]')}>
          {problem && (
            <p className="px-1 pb-1.5 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {problem}
            </p>
          )}

          <Composer
            value={draft}
            onChange={setDraft}
            onSend={() => send({ text: draft })}
            onStop={() => abort.current?.abort()}
            busy={busy}
            disabledReason={disabledReason}
            model={model}
            maxLength={MAX_MESSAGE_LENGTH}
            inputRef={composer}
          />
        </div>
      </div>

      <div aria-live="polite" className="sr-only">
        {announced}
      </div>

      <Dialog open={drawer} onOpenChange={setDrawer}>
        <DialogContent
          title="Conversations"
          className="top-0 left-0 h-full max-w-[17rem] translate-x-0 translate-y-0 rounded-none rounded-r-xl"
          bodyClassName="flex min-h-0 flex-1 flex-col px-3 py-3"
        >
          {list}
        </DialogContent>
      </Dialog>
    </div>
  )
}

/** What this conversation has used, out of the way until it is asked for. */
function Spend({ conversationId }: { conversationId: string }) {
  const [open, setOpen] = useState(false)
  const [spent, setSpent] = useState<AiSpent | null>(null)

  return (
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        if (next) api.chatConversationSpend(conversationId).then(setSpent).catch(() => undefined)
      }}
    >
      <Popover.Trigger asChild>
        <Button size="icon-sm" variant="ghost" aria-label="Spend">
          <Coins className="size-4" />
        </Button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={4}
          className="z-50 w-52 rounded-xl border bg-popover p-3 text-popover-foreground shadow-md"
          style={{ fontSize: 'var(--text-small)' }}
        >
          {spent ? (
            <dl className="flex flex-col gap-1">
              <div className="flex justify-between gap-2">
                <dt className="text-muted-foreground">Cost</dt>
                <dd>{spentText(spent)}</dd>
              </div>
              <div className="flex justify-between gap-2" title={tokensTitle(spent)}>
                <dt className="text-muted-foreground">Tokens</dt>
                <dd>{tokensText(spent)}</dd>
              </div>
            </dl>
          ) : (
            <span className="text-muted-foreground">Loading…</span>
          )}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  )
}

function Skeleton() {
  return (
    <div className="flex flex-col gap-4">
      {[70, 45, 90, 60].map((width, index) => (
        <div
          key={index}
          className={cn('h-4 animate-pulse rounded-md bg-muted motion-reduce:animate-none', index % 2 === 0 && 'ml-auto')}
          style={{ width: `${width}%` }}
        />
      ))}
    </div>
  )
}

function indexOfId(messages: ChatMessage[], id: number): number {
  return messages.findIndex((m) => m.id === id)
}
