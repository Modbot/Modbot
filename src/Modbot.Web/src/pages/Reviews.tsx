import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { SubjectLink } from '@/components/facts'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type Person, type ReviewEvidence, type ReviewList, type ReviewView } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * Reviews of a moderator's pattern (spec 5.8.5).
 *
 * Every review here is a question, and the page is built to keep it one. The evidence is shown
 * in words with the numbers in them -- "14 kicks in a day; their usual is 3" -- and the rule each
 * check follows is printed at the bottom, so a reader can see how the question came to be asked
 * and disagree with it. Closing needs a note, because "looked at it, it was fine" is the record
 * the spec wants kept; the note is recorded as a fact against the account that wrote it.
 */
export function Reviews({
  onOpenSubject,
  onChanged,
}: {
  onOpenSubject: (id: string) => void
  /** Called after a review closes, so the nav badge can catch up. */
  onChanged?: () => void
}) {
  const [state, setState] = useState<'open' | 'closed'>('open')
  const [list, setList] = useState<ReviewList | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api
      .reviews(state)
      .then((next) => {
        setList(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to review.'
            : 'Could not load the reviews.',
        ),
      )
  }, [state])

  // The previous list stays on screen until the next arrives; a blank flash between the two
  // tabs would suggest something was lost.
  useEffect(() => {
    load()
  }, [load])

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <div role="group" className="flex gap-0.5 rounded-md border bg-secondary p-0.5" style={{ borderWidth: 'var(--hairline)' }}>
          {(
            [
              { value: 'open', label: list ? `Waiting (${list.openCount})` : 'Waiting' },
              { value: 'closed', label: 'Closed' },
            ] as const
          ).map((o) => (
            <button
              key={o.value}
              type="button"
              aria-pressed={state === o.value}
              onClick={() => setState(o.value)}
              className={cn(
                'rounded px-3 font-medium transition-colors',
                state === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
              )}
              style={{ fontSize: 'var(--text-small)', height: 'calc(var(--control-h) - 6px)' }}
            >
              {o.label}
            </button>
          ))}
        </div>
        <span className="flex-1" />
        {list && (
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {list.lastRunAt
              ? `Detection last ran ${ago(list.lastRunAt, list.now)}`
              : 'Detection has not run yet'}
          </span>
        )}
      </div>

      {!list && (
        <Card>
          <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
        </Card>
      )}

      {list && list.reviews.length === 0 && (
        <Card>
          <CardContent className="py-10 text-center text-muted-foreground">
            {state === 'open' ? 'Nothing waiting.' : 'Nothing closed yet.'}
          </CardContent>
        </Card>
      )}

      {list?.reviews.map((review) => (
        <ReviewCard
          key={review.id}
          review={review}
          now={list.now}
          onOpenSubject={onOpenSubject}
          onClosed={() => {
            load()
            onChanged?.()
          }}
        />
      ))}

      {list && (
        <Card>
          <CardContent className="py-4">
            <div className="mb-2 font-medium">How a review opens</div>
            <dl className="flex flex-col gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {list.signals.map((s) => (
                <div key={s.signal}>
                  <dt className="font-medium text-foreground">{s.label}</dt>
                  <dd>{s.rule}</dd>
                </div>
              ))}
              <div>
                <dt className="font-medium text-foreground">What “usual” means</dt>
                <dd>{list.howUsualIsMeasured}</dd>
              </div>
            </dl>
          </CardContent>
        </Card>
      )}
    </div>
  )
}

function ReviewCard({
  review,
  now,
  onOpenSubject,
  onClosed,
}: {
  review: ReviewView
  now: string
  onOpenSubject: (id: string) => void
  onClosed: () => void
}) {
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [showFacts, setShowFacts] = useState(false)

  const close = () => {
    if (!note.trim()) {
      setProblem('Write a note first.')
      return
    }
    setBusy(true)
    setProblem(null)
    api
      .closeReview(review.id, note.trim())
      .then(() => onClosed())
      .catch((e: unknown) =>
        setProblem(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to close reviews.'
            : e instanceof ApiError && e.status === 409
              ? 'Somebody else closed this review a moment ago.'
              : 'Could not close the review.',
        ),
      )
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardContent className="py-4">
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span className="font-medium">{review.signalLabel}</span>
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <SubjectLink id={review.moderator.id} name={review.moderator.name} onOpen={onOpenSubject} />
            {review.aboutPerson && (
              <>
                {' '}and{' '}
                <SubjectLink id={review.aboutPerson.id} name={review.aboutPerson.name} onOpen={onOpenSubject} />
              </>
            )}
          </span>
          <span className="flex-1" />
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {review.state === 'Open'
              ? `Opened ${ago(review.openedAt, now)}${review.updatedAt !== review.openedAt ? `, numbers updated ${ago(review.updatedAt, now)}` : ''}`
              : `Closed ${ago(review.closedAt ?? review.updatedAt, now)} by ${review.closedByUsername ?? 'somebody'}`}
          </span>
        </div>

        <p className="mt-2 max-w-3xl">{review.summary}</p>

        <Evidence review={review} onOpenSubject={onOpenSubject} />

        <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span>
            Covers {formatDay(review.windowStart)}
            {formatDay(review.windowStart) !== formatDay(review.windowEnd) ? ` – ${formatDay(review.windowEnd)}` : ''}
          </span>
          <button type="button" className="hover:underline" onClick={() => setShowFacts((v) => !v)}>
            {showFacts ? 'Hide' : 'Show'} the {review.evidence.factIds.length} fact id{review.evidence.factIds.length === 1 ? '' : 's'}
          </button>
        </div>
        {showFacts && (
          <p className="mt-1 break-all font-mono text-muted-foreground" style={{ fontSize: '0.6875rem' }}>
            {review.evidence.factIds.join(', ')}
          </p>
        )}

        {review.state === 'Closed' ? (
          <div className="mt-3 rounded-md bg-muted/40 px-3 py-2" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">{review.closedByUsername ?? 'Somebody'} wrote: </span>
            <span className="whitespace-pre-wrap break-words">{review.note}</span>
          </div>
        ) : (
          <div className="mt-3 flex flex-col gap-2">
            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">What did you conclude?</span>
              <textarea
                className="min-h-16 rounded-md border bg-background px-2 py-1"
                style={{ borderWidth: 'var(--hairline)' }}
                value={note}
                onChange={(e) => setNote(e.target.value)}
                maxLength={2000}
              />
            </label>
            <div className="flex items-center gap-2">
              <Button size="xs" onClick={close} disabled={busy}>
                Close review
              </Button>
              {problem && (
                <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                  {problem}
                </span>
              )}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

const KIND_WORDS: Record<string, string> = {
  instanceKicks: 'instance kicks',
  warns: 'warns',
  bans: 'bans',
  removals: 'removed from the group',
  rejections: 'join requests turned away',
}

/** The numbers, laid out, so the sentence above can be checked against them. */
function Evidence({ review, onOpenSubject }: { review: ReviewView; onOpenSubject: (id: string) => void }) {
  const e: ReviewEvidence = review.evidence
  const kinds = Object.entries(e.byKind).filter(([, n]) => n > 0)

  return (
    <dl className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      <Pair label="Actions" value={String(e.actions)} />
      {kinds.length > 0 && (
        <Pair label="Of which" value={kinds.map(([k, n]) => `${n} ${KIND_WORDS[k] ?? k}`).join(', ')} />
      )}
      {e.places !== undefined && <Pair label="Different instances or days" value={String(e.places)} />}
      {e.otherModerators !== undefined && <Pair label="Other moderators who acted on them" value={String(e.otherModerators)} />}
      {e.windowDays !== undefined && <Pair label="Looking back" value={`${e.windowDays} days`} />}
      {e.day !== undefined && <Pair label="Day (UTC)" value={e.day} />}
      {e.nextBusiest !== undefined && (
        <Pair
          label="Next busiest that day"
          value={
            e.nextBusiest ? (
              <>
                <PersonLink person={{ platform: 'vrchat', id: e.nextBusiest.moderatorId, name: null }} onOpen={onOpenSubject} /> with{' '}
                {e.nextBusiest.actions}
              </>
            ) : (
              'nobody else did anything'
            )
          }
        />
      )}
      {e.teamUsualPerDay !== undefined && <Pair label="Team’s usual per moderator-day" value={fixed(e.teamUsualPerDay)} />}
      {e.ownUsualPerDay !== undefined && (
        <Pair
          label="Their usual"
          value={e.ownUsualPerDay === null ? 'none yet' : `${fixed(e.ownUsualPerDay)} a day over ${e.ownActiveDays ?? '?'} active days`}
        />
      )}
      <Pair
        label="Rule applied"
        value={Object.entries(e.threshold)
          .map(([k, v]) => `${THRESHOLD_WORDS[k] ?? k} ${fixed(v)}`)
          .join(' · ')}
      />
    </dl>
  )
}

const THRESHOLD_WORDS: Record<string, string> = {
  actions: 'at least',
  actionsWhenNobodyElseActed: 'when nobody else acted:',
  actionsWhenOthersActed: 'when others acted:',
  places: 'places at least',
  minActions: 'at least',
  multiplier: 'times',
  bar: 'so the bar was',
}

const fixed = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(1))

function PersonLink({ person, onOpen }: { person: Person; onOpen: (id: string) => void }) {
  return <SubjectLink id={person.id} name={person.name} onOpen={onOpen} className="text-foreground" />
}

function Pair({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex gap-1.5">
      <dt>{label}:</dt>
      <dd className="text-foreground">{value}</dd>
    </div>
  )
}
