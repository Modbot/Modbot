import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Markdown } from '@/components/Markdown'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  api,
  ApiError,
  type ChatConversationSummary,
  type ChatMessage,
  type ChatReference,
  type ChatStreamEvent,
  type ChatToolCall,
} from '@/lib/api'
import { openInstance, openPerson, openWorld } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { PageMessage } from '@/pages/analytics/shared'

/** How many things one tool call lists before the rest fold behind a count. */
const REFERENCES_SHOWN = 8

/**
 * Chat -- questions answered by the configured model, from Modbot's own data (AI chat design).
 *
 * The reply streams in as it is written. Tool calls appear as chips; the people, worlds and rooms a
 * tool found open the same popups as everywhere else. Conversations are the signed-in person's own.
 */
export function Chat() {
  const [available, setAvailable] = useState<boolean | null>(null)
  const [conversations, setConversations] = useState<ChatConversationSummary[]>([])
  const [error, setError] = useState<string | null>(null)

  const [current, setCurrent] = useState<string | null>(null)
  const [full, setFull] = useState(false)
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [loading, setLoading] = useState(false)

  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [streamed, setStreamed] = useState('')
  const [running, setRunning] = useState<{ callId: string; label: string }[]>([])
  const [problem, setProblem] = useState<string | null>(null)

  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement | null>(null)

  const loadHome = useCallback(
    () =>
      api
        .chatHome()
        .then((home) => {
          setAvailable(home.available)
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

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' })
  }, [messages, streamed, running])

  const open = (id: string) => {
    if (busy) return
    setCurrent(id)
    setMessages([])
    setProblem(null)
    setLoading(true)

    api
      .chatConversation(id)
      .then((c) => {
        setMessages(c.messages)
        setFull(c.full)
      })
      .catch(() => setProblem('Could not load this conversation.'))
      .finally(() => setLoading(false))
  }

  const startNew = () => {
    if (busy) return
    setCurrent(null)
    setMessages([])
    setFull(false)
    setProblem(null)
  }

  const remove = () => {
    if (!current || busy) return
    const id = current

    api
      .deleteChatConversation(id)
      .then(() => {
        setConversations((list) => list.filter((c) => c.id !== id))
        startNew()
      })
      .catch(() => setProblem('Could not delete this conversation.'))
  }

  const send = () => {
    const text = draft.trim()
    if (!text || busy) return

    setBusy(true)
    setProblem(null)
    setStreamed('')
    setRunning([])
    setDraft('')

    const controller = new AbortController()
    abort.current = controller

    const onEvent = (event: ChatStreamEvent) => {
      switch (event.type) {
        case 'conversation':
          setCurrent(event.data.id)
          setConversations((list) => [event.data, ...list.filter((c) => c.id !== event.data.id)])
          break
        case 'message':
          setMessages((list) => [...list, event.data])
          if (event.data.role === 'assistant') setStreamed('')
          if (event.data.role === 'tool')
            setRunning((list) => list.filter((r) => r.callId !== event.data.toolCallId))
          break
        case 'text':
          setStreamed((s) => s + event.data.text)
          break
        case 'tool':
          setRunning((list) => [...list, { callId: event.data.callId, label: event.data.label }])
          break
        case 'done':
          if (event.data.error) setProblem(event.data.error)
          break
      }
    }

    api
      .sendChatMessage({ conversationId: current, text }, onEvent, controller.signal)
      .catch((e: unknown) => {
        if (controller.signal.aborted) return
        setProblem(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
        if (e instanceof ApiError && e.status === 409 && /full/i.test(e.message)) setFull(true)
        if (e instanceof ApiError && e.status !== 0) setDraft(text)
      })
      .finally(() => {
        setBusy(false)
        setStreamed('')
        setRunning([])
        abort.current = null
      })
  }

  if (error) return <PageMessage>{error}</PageMessage>
  if (available === null) return <PageMessage>Loading…</PageMessage>
  if (!available && conversations.length === 0) return <PageMessage>Chat is off.</PageMessage>

  const title = conversations.find((c) => c.id === current)?.title

  return (
    <div className="grid gap-4 lg:grid-cols-[14rem_1fr]">
      <nav aria-label="Conversations" className="flex min-w-0 flex-col gap-2">
        <Button size="sm" variant="outline" disabled={busy} onClick={startNew}>
          New conversation
        </Button>
        <ul className="flex flex-col gap-0.5" style={{ fontSize: 'var(--text-small)' }}>
          {conversations.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                disabled={busy}
                onClick={() => open(c.id)}
                aria-current={c.id === current ? 'true' : undefined}
                className={cn(
                  'w-full truncate rounded-md px-2 py-1.5 text-left transition-colors',
                  c.id === current ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground',
                )}
                title={c.title}
              >
                {c.title}
              </button>
            </li>
          ))}
        </ul>
      </nav>

      <Card className="min-w-0">
        <CardContent className="flex h-[calc(100vh-12.5rem)] min-h-[24rem] flex-col gap-3">
          {current && (
            <div className="flex items-center justify-between gap-3">
              <h2 className="truncate font-medium">{title}</h2>
              <Button size="sm" variant="ghost" disabled={busy} onClick={remove}>
                Delete
              </Button>
            </div>
          )}

          <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto pr-1" aria-live="polite">
            {loading && <p className="text-muted-foreground">Loading…</p>}
            <Messages messages={messages} />
            {(streamed || running.length > 0) && (
              <div className="flex flex-col gap-2">
                {running.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {running.map((r) => (
                      <Chip key={r.callId} muted>
                        {r.label}…
                      </Chip>
                    ))}
                  </div>
                )}
                {streamed && <Markdown text={streamed} />}
              </div>
            )}
            {busy && !streamed && running.length === 0 && <p className="text-muted-foreground">…</p>}
            <div ref={bottom} />
          </div>

          {problem && (
            <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {problem}
            </p>
          )}

          <form
            className="flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              send()
            }}
          >
            <textarea
              aria-label="Message"
              rows={2}
              value={draft}
              disabled={!available || full}
              maxLength={4000}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  send()
                }
              }}
              className={cn(
                'border-input placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50',
                'dark:bg-input/30 flex min-h-10 w-full resize-none rounded-md border bg-transparent px-3 py-2 text-base shadow-xs',
                'transition-[color,box-shadow] outline-none focus-visible:ring-[3px] disabled:opacity-50 md:text-sm',
              )}
            />
            {busy ? (
              <Button type="button" variant="outline" onClick={() => abort.current?.abort()}>
                Stop
              </Button>
            ) : (
              <Button type="submit" disabled={!available || full || !draft.trim()}>
                Send
              </Button>
            )}
          </form>
        </CardContent>
      </Card>
    </div>
  )
}

function Messages({ messages }: { messages: ChatMessage[] }) {
  // A tool's result is shown on the chip for the call it answers, not as a message of its own.
  const results = useMemo(() => {
    const byCall = new Map<string, ChatMessage>()
    for (const m of messages) if (m.role === 'tool' && m.toolCallId) byCall.set(m.toolCallId, m)
    return byCall
  }, [messages])

  return (
    <>
      {messages.map((m) => {
        if (m.role === 'tool') return null

        if (m.role === 'user') {
          return (
            <div key={m.id} className="ml-auto max-w-[85%] rounded-lg bg-secondary px-3 py-2 whitespace-pre-wrap">
              {m.content}
            </div>
          )
        }

        return (
          <div key={m.id} className="flex flex-col gap-2">
            {m.content && <Markdown text={m.content} />}
            {m.toolCalls.length > 0 && (
              <div className="flex flex-col gap-1.5">
                {m.toolCalls.map((call) => (
                  <ToolCallRow key={call.id} call={call} result={results.get(call.id)} />
                ))}
              </div>
            )}
          </div>
        )
      })}
    </>
  )
}

function ToolCallRow({ call, result }: { call: ChatToolCall; result: ChatMessage | undefined }) {
  const [all, setAll] = useState(false)
  const references = result?.references ?? []
  const shown = all ? references : references.slice(0, REFERENCES_SHOWN)
  const only = references.length === 1 ? references[0] : null

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <Chip
        title={call.arguments}
        muted={!result}
        problem={result?.worked === false}
        onClick={only ? () => openReference(only) : undefined}
      >
        {call.label}
        {result?.worked === false && ' · failed'}
      </Chip>
      {!only &&
        shown.map((r) => (
          <Chip key={`${r.kind}:${r.id}`} link title={r.id} onClick={() => openReference(r)}>
            {r.label ?? r.id}
          </Chip>
        ))}
      {!only && !all && references.length > REFERENCES_SHOWN && (
        <Chip link onClick={() => setAll(true)}>
          +{references.length - REFERENCES_SHOWN}
        </Chip>
      )}
    </div>
  )
}

function openReference(r: ChatReference) {
  if (r.kind === 'person') openPerson(r.id)
  else if (r.kind === 'world') openWorld(r.id)
  else openInstance(r.id)
}

function Chip({
  children,
  title,
  muted,
  problem,
  link,
  onClick,
}: {
  children: React.ReactNode
  title?: string
  muted?: boolean
  problem?: boolean
  link?: boolean
  onClick?: () => void
}) {
  const className = cn(
    'inline-flex max-w-[16rem] items-center truncate rounded-full border px-2.5 py-0.5',
    problem ? 'border-destructive/40 text-destructive' : muted ? 'text-muted-foreground' : '',
    link ? 'bg-secondary font-medium' : '',
    onClick && 'hover:bg-accent hover:text-accent-foreground focus-visible:outline-2 focus-visible:outline-ring',
  )
  const style = { fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }

  return onClick ? (
    <button type="button" title={title} className={className} style={style} onClick={onClick}>
      {children}
    </button>
  ) : (
    <span title={title} className={className} style={style}>
      {children}
    </span>
  )
}
