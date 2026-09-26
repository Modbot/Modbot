import { useMemo, useState } from 'react'
import { Check, ChevronLeft, ChevronRight, Copy, Pencil, RotateCcw } from 'lucide-react'
import { Answer } from '@/components/chat/Answer'
import { Sources } from '@/components/chat/Sources'
import { ToolSteps, type ToolStep } from '@/components/chat/ToolSteps'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import type { ChatMessage, ChatReference } from '@/lib/api'
import { cn } from '@/lib/utils'
import { dateTime } from '@/components/charts/format'

/** A question, or the reply written after it: what one row of the conversation shows. */
type Block =
  | { kind: 'question'; message: ChatMessage }
  | { kind: 'reply'; messages: ChatMessage[] }

/** Inside a reply: the lookups it made, and the text it wrote, in the order they happened. */
type Part = { kind: 'steps'; steps: ToolStep[] } | { kind: 'text'; message: ChatMessage }

export function Thread({
  messages,
  streamed,
  stopped,
  writing,
  busy,
  onRetry,
  onEdit,
  onReadVersion,
}: {
  messages: ChatMessage[]
  /** The reply being written, as far as it has got. */
  streamed: string
  /** True when `streamed` is a reply that was stopped rather than one still being written. */
  stopped: boolean
  /** True while a reply is being written, including before the first word arrives. */
  writing: boolean
  /** True while anything is in flight, which is when the actions are put away. */
  busy: boolean
  onRetry: (afterMessageId: number) => void
  onEdit: (messageId: number, text: string) => void
  onReadVersion: (messageId: number) => void
}) {
  const blocks = useMemo(() => blocksOf(messages), [messages])

  const lastQuestion = [...blocks].reverse().find((b) => b.kind === 'question')?.message.id ?? null
  const lastReply = blocks.at(-1)?.kind === 'reply' ? blocks.length - 1 : null

  return (
    <div className="flex flex-col gap-6">
      {blocks.map((block, index) =>
        block.kind === 'question' ? (
          <Question
            key={block.message.id}
            message={block.message}
            editable={!busy && block.message.id === lastQuestion}
            onEdit={onEdit}
            onReadVersion={onReadVersion}
          />
        ) : (
          <Reply
            key={block.messages[0].id}
            messages={block.messages}
            retryable={!busy && index === lastReply}
            onRetry={onRetry}
            onReadVersion={onReadVersion}
          />
        ),
      )}

      {writing && <Writing text={streamed} stopped={stopped} />}
    </div>
  )
}

function Question({
  message,
  editable,
  onEdit,
  onReadVersion,
}: {
  message: ChatMessage
  editable: boolean
  onEdit: (messageId: number, text: string) => void
  onReadVersion: (messageId: number) => void
}) {
  const [editing, setEditing] = useState(false)

  if (editing) {
    return (
      <Editor
        text={message.content}
        onCancel={() => setEditing(false)}
        onSend={(text) => {
          setEditing(false)
          onEdit(message.id, text)
        }}
      />
    )
  }

  return (
    <div className="group/turn ml-auto flex max-w-[85%] flex-col items-end gap-1">
      <div
        className="rounded-sm border border-(length:--hairline) bg-strip px-3 py-2 whitespace-pre-wrap"
        title={when(message.createdAt)}
      >
        {message.content}
      </div>
      <Actions>
        <Versions message={message} onReadVersion={onReadVersion} />
        <CopyAction text={message.content} />
        {editable && (
          <Action label="Edit" onClick={() => setEditing(true)}>
            <Pencil className="size-3.5" />
          </Action>
        )}
      </Actions>
    </div>
  )
}

/** A question being edited. Mounted when editing starts, so it opens on what is there. */
function Editor({
  text,
  onCancel,
  onSend,
}: {
  text: string
  onCancel: () => void
  onSend: (text: string) => void
}) {
  const [draft, setDraft] = useState(text)

  return (
    <div className="ml-auto flex w-full max-w-[85%] flex-col gap-2">
      <Textarea
        autoFocus
        aria-label="Message"
        value={draft}
        maxLength={4000}
        onChange={(e) => setDraft(e.target.value)}
        className="min-h-20 resize-none"
      />
      <div className="flex justify-end gap-2">
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button size="sm" disabled={draft.trim().length === 0} onClick={() => onSend(draft.trim())}>
          Send
        </Button>
      </div>
    </div>
  )
}

function Reply({
  messages,
  retryable,
  onRetry,
  onReadVersion,
}: {
  messages: ChatMessage[]
  retryable: boolean
  onRetry: (afterMessageId: number) => void
  onReadVersion: (messageId: number) => void
}) {
  const parts = useMemo(() => partsOf(messages), [messages])
  const references = useMemo(() => referencesOf(messages), [messages])

  const text = messages
    .filter((m) => m.role === 'assistant' && m.content.length > 0)
    .map((m) => m.content)
    .join('\n\n')

  const first = messages[0]
  const stopped = messages.some((m) => m.stopped)

  return (
    <div className="group/turn flex flex-col gap-1">
      {parts.map((part, index) =>
        part.kind === 'steps' ? (
          <ToolSteps key={`steps-${index}`} steps={part.steps} />
        ) : (
          <Answer key={part.message.id} text={part.message.content} references={references} />
        ),
      )}

      {stopped && (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Stopped
        </span>
      )}

      <Sources references={references} />

      <Actions>
        <Versions message={first} onReadVersion={onReadVersion} />
        {text.length > 0 && <CopyAction text={text} />}
        {retryable && first.parentId !== null && (
          <Action label="Try again" onClick={() => onRetry(first.parentId!)}>
            <RotateCcw className="size-3.5" />
          </Action>
        )}
        <span className="font-mono whitespace-nowrap text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {when(first.createdAt)}
        </span>
      </Actions>
    </div>
  )
}

/** The reply as it arrives: the words so far, or a mark while there are none. */
function Writing({ text, stopped }: { text: string; stopped: boolean }) {
  return (
    <div className="flex flex-col gap-1">
      {text ? (
        <Answer text={text} references={[]} />
      ) : (
        <span className="inline-block size-2 animate-pulse bg-muted-foreground motion-reduce:animate-none" />
      )}
      {stopped && (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Stopped
        </span>
      )}
    </div>
  )
}

function Actions({ children }: { children: React.ReactNode }) {
  return (
    <div
      className={cn(
        'flex flex-wrap items-center gap-1 opacity-0 transition-opacity motion-reduce:transition-none',
        'focus-within:opacity-100 group-hover/turn:opacity-100 [@media(hover:none)]:opacity-100',
      )}
    >
      {children}
    </div>
  )
}

function Action({
  label,
  onClick,
  children,
}: {
  label: string
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <Button type="button" variant="ghost" size="icon-xs" aria-label={label} title={label} onClick={onClick}>
      {children}
    </Button>
  )
}

function CopyAction({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)

  return (
    <Action
      label={copied ? 'Copied' : 'Copy'}
      onClick={() => {
        void navigator.clipboard?.writeText(text).then(() => {
          setCopied(true)
          window.setTimeout(() => setCopied(false), 1500)
        })
      }}
    >
      {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
    </Action>
  )
}

/** `‹ 1/2 ›` — which version of this message is being read, and the way to the others. */
function Versions({ message, onReadVersion }: { message: ChatMessage; onReadVersion: (id: number) => void }) {
  if (message.versions.length < 2) return null

  const at = message.versions.indexOf(message.id)

  return (
    <span className="flex items-center gap-0.5 font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        aria-label="Previous version"
        disabled={at <= 0}
        onClick={() => onReadVersion(message.versions[at - 1])}
      >
        <ChevronLeft className="size-3.5" />
      </Button>
      {at + 1}/{message.versions.length}
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        aria-label="Next version"
        disabled={at < 0 || at >= message.versions.length - 1}
        onClick={() => onReadVersion(message.versions[at + 1])}
      >
        <ChevronRight className="size-3.5" />
      </Button>
    </span>
  )
}

/** Questions on their own; everything the model sent after one belongs to that reply. */
function blocksOf(messages: ChatMessage[]): Block[] {
  const blocks: Block[] = []

  for (const message of messages) {
    if (message.role === 'user') {
      blocks.push({ kind: 'question', message })
      continue
    }

    const last = blocks.at(-1)
    if (last?.kind === 'reply') last.messages.push(message)
    else blocks.push({ kind: 'reply', messages: [message] })
  }

  return blocks
}

/** A reply, in order: each round's lookups, then what it wrote. */
function partsOf(messages: ChatMessage[]): Part[] {
  const results = new Map<string, ChatMessage>()
  for (const m of messages) if (m.role === 'tool' && m.toolCallId) results.set(m.toolCallId, m)

  const parts: Part[] = []

  for (const message of messages) {
    if (message.role !== 'assistant') continue

    if (message.toolCalls.length > 0) {
      const steps = message.toolCalls.map((call) => ({ call, result: results.get(call.id) }))
      const last = parts.at(-1)

      // Round after round of lookups reads as one run of steps, not as several groups.
      if (last?.kind === 'steps') last.steps.push(...steps)
      else parts.push({ kind: 'steps', steps })
    }

    if (message.content.length > 0) parts.push({ kind: 'text', message })
  }

  return parts
}

/** Everything the reply's lookups found, for the names in the text to be matched against. */
function referencesOf(messages: ChatMessage[]): ChatReference[] {
  return messages.flatMap((m) => m.references)
}

const when = dateTime
