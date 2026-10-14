import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { useDiscordChannels } from '@/lib/discordLists'
import { formatDay } from '@/lib/format'
import { optionLines, voteApi, type Vote, type VoteList } from '@/lib/votes'

/**
 * Questions Modbot posts in a Discord channel with a button for each option. Reading needs View
 * posts; posting and closing need Manage posts, which the server reports as `canManage`.
 */
export function Votes() {
  const [list, setList] = useState<VoteList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [making, setMaking] = useState(false)
  const { data: channels } = useDiscordChannels()

  const load = useCallback(() => {
    return voteApi
      .list()
      .then((next) => {
        setList(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : 'Could not load the votes.'),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const channelName = (id: string) => {
    const found = channels?.channels.find((c) => c.id === id)
    return found ? `#${found.name}` : 'a channel'
  }

  return (
    <div className="flex flex-col gap-3">
      {list?.canManage && !making && (
        <div className="flex">
          <Button size="sm" onClick={() => setMaking(true)}>
            New vote
          </Button>
        </div>
      )}

      {making && list && (
        <Card>
          <CardHeader>
            <CardTitle>New vote</CardTitle>
          </CardHeader>
          <NewVoteForm
            list={list}
            onCancel={() => setMaking(false)}
            onPosted={() => {
              setMaking(false)
              load()
            }}
          />
        </Card>
      )}

      {error && (
        <Card>
          <EmptyRow tone="danger" onTryAgain={load}>{error}</EmptyRow>
        </Card>
      )}

      {!error && !list && (
        <Card>
          <EmptyRow tone="loading" />
        </Card>
      )}

      {!error && list && list.votes.length === 0 && (
        <Card>
          <EmptyRow>No votes yet.</EmptyRow>
        </Card>
      )}

      {!error &&
        list?.votes.map((vote) => (
          <VoteCard key={vote.id} vote={vote} where={channelName(vote.channelId)} canManage={list.canManage} onChanged={load} />
        ))}
    </div>
  )
}

function NewVoteForm({ list, onPosted, onCancel }: { list: VoteList; onPosted: () => void; onCancel: () => void }) {
  const [question, setQuestion] = useState('')
  const [channelId, setChannelId] = useState('')
  const [roleId, setRoleId] = useState('')
  const [options, setOptions] = useState('')
  const [showCounts, setShowCounts] = useState(false)
  const [anonymous, setAnonymous] = useState(true)
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const lines = optionLines(options)
  const ready = question.trim().length > 0 && channelId !== '' && lines.length >= 2

  const send = () => {
    setSending(true)
    setProblem(null)

    voteApi
      .create({ question: question.trim(), channelId, roleId: roleId || null, options: lines, showCounts, anonymous })
      .then(onPosted)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not post the vote.'))
      .finally(() => setSending(false))
  }

  return (
    <CardContent className="flex flex-col gap-2">
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Question</span>
        <Input value={question} maxLength={list.mostQuestionLength} onChange={(e) => setQuestion(e.target.value)} autoFocus />
      </label>

      <ChannelPicker
        label="Channel"
        value={channelId}
        onChange={setChannelId}
        needs={['viewChannel', 'sendMessages', 'embedLinks']}
        allowNone={false}
      />

      <RolePicker label="Only for role" value={roleId} onChange={setRoleId} />

      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Options, one on each line</span>
        <Textarea className="min-h-24" value={options} onChange={(e) => setOptions(e.target.value)} />
      </label>

      <Checkbox checked={showCounts} onChange={setShowCounts}>
        Show counts while open
      </Checkbox>
      <Checkbox checked={anonymous} onChange={setAnonymous}>
        Anonymous
      </Checkbox>

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={send} disabled={sending || !ready}>
          {sending ? 'Posting…' : 'Post vote'}
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel} disabled={sending}>
          Cancel
        </Button>
        {problem && (
          <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            {problem}
          </span>
        )}
      </div>
    </CardContent>
  )
}

function VoteCard({
  vote,
  where,
  canManage,
  onChanged,
}: {
  vote: Vote
  where: string
  canManage: boolean
  onChanged: () => void
}) {
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const close = () => {
    setBusy(true)
    setProblem(null)

    voteApi
      .close(vote.id)
      .then((closed) => {
        setAsking(false)
        // Closed in Modbot even when Discord refused the rewrite, so the list is read either way.
        if (closed.messageProblem) setProblem(`Closed, but the Discord message was not updated: ${closed.messageProblem}`)
        onChanged()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not close the vote.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="break-words">{vote.question}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-center gap-2 text-muted-foreground">
          <Badge variant={vote.status === 'open' ? 'ok' : 'secondary'}>{vote.status === 'open' ? 'Open' : 'Closed'}</Badge>
          <span>{where}</span>
          <span className="font-mono">{formatDay(vote.createdAt)}</span>
          <span>
            <span className="font-mono">{vote.total}</span> {vote.total === 1 ? 'vote' : 'votes'}
          </span>
          {vote.anonymous && <span>Anonymous</span>}
          {vote.showCounts && <span>Counts shown</span>}
          <span className="flex-1" />
          {canManage && vote.status === 'open' && !asking && (
            <Button size="xs" variant="outline" onClick={() => setAsking(true)}>
              Close vote
            </Button>
          )}
          {asking && (
            <>
              <Button size="xs" disabled={busy} onClick={close}>
                {busy ? 'Closing…' : 'Close for good'}
              </Button>
              <Button size="xs" variant="ghost" disabled={busy} onClick={() => setAsking(false)}>
                Keep open
              </Button>
            </>
          )}
        </div>

        <ul className="flex flex-col gap-1">
          {vote.options.map((option) => (
            <li key={option.id} className="flex flex-col">
              <div className="flex items-baseline justify-between gap-3">
                <span className="min-w-0 break-words">{option.label}</span>
                <span className="font-mono">
                  {option.count}
                  {vote.total > 0 && ` · ${Math.round((option.count * 100) / vote.total)}%`}
                </span>
              </div>
              {option.voters && option.voters.length > 0 && (
                <span className="break-words text-muted-foreground">{option.voters.map((v) => v.name).join(', ')}</span>
              )}
            </li>
          ))}
        </ul>

        {problem && <p className="text-destructive">{problem}</p>}
      </CardContent>
    </Card>
  )
}
