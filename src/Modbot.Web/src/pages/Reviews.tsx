import { useCallback, useEffect, useState } from 'react'
import { changesReviews } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Textarea } from '@/components/ui/textarea'
import { SubjectLink } from '@/components/facts'
import { ago, formatDay, needsYear } from '@/lib/format'
import { api, ApiError, type Person, type ReviewEvidence, type ReviewList, type ReviewView } from '@/lib/api'
import { PROPOSED_ACTION_LABELS, type ProposedAction } from '@/lib/autoMod'
import { Row } from '@/components/ui/fact-row'

/**
 * Reviews of a moderator's pattern (spec 5.8.5).
 *
 * Every review here is a question, and the page is built to keep it one. The evidence is shown
 * in words with the numbers in them -- "14 kicks in a day; their usual is 3" -- so a reader can
 * see how the question came to be asked and disagree with it. Closing needs a note, because "looked at it, it was fine" is the record
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

  // And again when the live stream says a review opened or closed.
  const live = useLiveVersion(changesReviews)

  // The previous list stays on screen until the next arrives; a blank flash between the two
  // tabs would suggest something was lost.
  useEffect(() => {
    load()
  }, [load, live])

  if (error) {
    return (
      <Card>
        <EmptyRow tone="danger">{error}</EmptyRow>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <SwitchBank
          value={state}
          onChange={setState}
          options={[
            {
              value: 'open',
              label: list ? (
                <>
                  Waiting <span className="font-mono">{list.openCount}</span>
                </>
              ) : (
                'Waiting'
              ),
            },
            { value: 'closed', label: 'Closed' },
          ]}
        />
        <span className="flex-1" />
        {list && (
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {list.lastRunAt ? (
              <>
                Detection last ran <span className="font-mono">{ago(list.lastRunAt, list.now)}</span>
              </>
            ) : (
              'Detection has not run yet'
            )}
          </span>
        )}
      </div>

      {!list && (
        <Card>
          <EmptyRow>Loading…</EmptyRow>
        </Card>
      )}

      {list && list.reviews.length === 0 && (
        <Card>
          <EmptyRow>{state === 'open' ? 'Nothing waiting.' : 'Nothing closed yet.'}</EmptyRow>
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

  const flagReview = review.signal === 'ai-flag'

  const close = (outcome?: 'right' | 'wrong') => {
    if (!note.trim()) {
      setProblem('Write a note first.')
      return
    }
    setBusy(true)
    setProblem(null)
    api
      .closeReview(review.id, note.trim(), outcome)
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
      <CardHeader className="items-baseline">
        <CardTitle>{review.signalLabel}</CardTitle>
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {/* The "moderator" of a flag review is the rule, not a person, so it is not a link. */}
          {flagReview ? (
            (review.evidence.ruleName ?? '')
          ) : (
            <SubjectLink id={review.moderator.id} name={review.moderator.name} onOpen={onOpenSubject} />
          )}
          {review.aboutPerson && (
            <>
              {' '}and{' '}
              <SubjectLink id={review.aboutPerson.id} name={review.aboutPerson.name} onOpen={onOpenSubject} />
            </>
          )}
        </span>
        <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {review.state === 'Open' ? (
            <>
              Opened <span className="font-mono">{ago(review.openedAt, now)}</span>
              {review.updatedAt !== review.openedAt && (
                <>
                  , numbers updated <span className="font-mono">{ago(review.updatedAt, now)}</span>
                </>
              )}
            </>
          ) : (
            <>
              Closed <span className="font-mono">{ago(review.closedAt ?? review.updatedAt, now)}</span> by{' '}
              {review.closedByUsername ?? 'somebody'}
            </>
          )}
        </span>
      </CardHeader>

      <CardContent>
        <p className="max-w-3xl">{review.summary}</p>

        {flagReview ? (
          <FlagEvidence review={review} />
        ) : (
          <>
            <Evidence review={review} onOpenSubject={onOpenSubject} />

            <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <span>
                Covers <span className="font-mono">{formatDay(review.windowStart, needsYear(review.windowStart, review.windowEnd))}</span>
                {formatDay(review.windowStart) !== formatDay(review.windowEnd) && (
                  <>
                    {' – '}
                    <span className="font-mono">{formatDay(review.windowEnd, needsYear(review.windowStart, review.windowEnd))}</span>
                  </>
                )}
              </span>
              <button type="button" className="hover:underline" onClick={() => setShowFacts((v) => !v)}>
                {showFacts ? 'Hide' : 'Show'} the {review.evidence.factIds.length} fact id{review.evidence.factIds.length === 1 ? '' : 's'}
              </button>
            </div>
            {showFacts && (
              <p className="mt-1 break-all font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                {review.evidence.factIds.join(', ')}
              </p>
            )}
          </>
        )}

        {review.state === 'Open' && (
          <label className="mt-3 flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">What did you conclude?</span>
            <Textarea
              className="min-h-16"
              value={note}
              onChange={(e) => setNote(e.target.value)}
              maxLength={2000}
            />
          </label>
        )}
      </CardContent>

      {review.state === 'Closed' ? (
        <CardFooter className="block" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">{review.closedByUsername ?? 'Somebody'} wrote: </span>
          <span className="whitespace-pre-wrap break-words">{review.note}</span>
          {review.outcome && (
            <span className="ml-2 text-muted-foreground">
              ({review.outcome === 'right' ? 'rule was right' : 'rule was wrong'})
            </span>
          )}
        </CardFooter>
      ) : (
        <CardFooter className="flex-wrap gap-2">
          {flagReview ? (
            <>
              <Button size="xs" onClick={() => close('right')} disabled={busy}>
                The rule was right
              </Button>
              <Button size="xs" variant="outline" onClick={() => close('wrong')} disabled={busy}>
                The rule was wrong
              </Button>
            </>
          ) : (
            <Button size="xs" onClick={() => close()} disabled={busy}>
              Close review
            </Button>
          )}
          {problem && (
            <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {problem}
            </span>
          )}
        </CardFooter>
      )}
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

/** What a review opened by a moderation flag was opened on (AI moderation design §19). */
function FlagEvidence({ review }: { review: ReviewView }) {
  const e = review.evidence

  return (
    <Facts>
      {e.ruleName && <Row label="Rule" value={`${e.ruleName} v${e.ruleVersion ?? 1}`} />}
      {e.target && <Row label="Checked" value={e.target} />}
      {e.language && <Row label="Language" value={e.language} />}
      {e.picture ? (
        <Row
          label="Picture"
          value={
            e.pictureUrl ? (
              <a href={e.pictureUrl} target="_blank" rel="noreferrer noopener" className="underline">
                {e.picture}
              </a>
            ) : (
              e.picture
            )
          }
        />
      ) : (
        e.matched && <Row label="Matched" value={`“${e.matched}”`} />
      )}
      {e.reason && <Row label="Reason" value={e.reason} />}
      {e.aiOpinion && (
        <Row label="AI" value={`${e.aiOpinion === 'keep' ? 'Keep' : 'Dismiss'}${e.aiOpinionReason ? ` — ${e.aiOpinionReason}` : ''}`} />
      )}
      {e.aiProposedAction && e.aiProposedAction !== 'none' && (
        <Row label="AI proposed" value={PROPOSED_ACTION_LABELS[e.aiProposedAction as ProposedAction] ?? e.aiProposedAction} />
      )}
    </Facts>
  )
}

/** The numbers, laid out, so the sentence above can be checked against them. */
function Evidence({ review, onOpenSubject }: { review: ReviewView; onOpenSubject: (id: string) => void }) {
  const e: ReviewEvidence = review.evidence
  const kinds = Object.entries(e.byKind).filter(([, n]) => n > 0)

  return (
    <Facts>
      <Row label="Actions" value={String(e.actions)} mono />
      {kinds.length > 0 && (
        <Row
          label="Of which"
          value={kinds.map(([k, n], i) => (
            <span key={k}>
              {i > 0 && ', '}
              <span className="font-mono">{n}</span> {KIND_WORDS[k] ?? k}
            </span>
          ))}
        />
      )}
      {e.places !== undefined && <Row label="Different instances or days" value={String(e.places)} mono />}
      {e.otherModerators !== undefined && (
        <Row label="Other moderators who acted on them" value={String(e.otherModerators)} mono />
      )}
      {e.windowDays !== undefined && (
        <Row
          label="Looking back"
          value={
            <>
              <span className="font-mono">{e.windowDays}</span> days
            </>
          }
        />
      )}
      {e.day !== undefined && <Row label="Day (UTC)" value={e.day} mono />}
      {e.nextBusiest !== undefined && (
        <Row
          label="Next busiest that day"
          value={
            e.nextBusiest ? (
              <>
                <PersonLink person={{ platform: 'vrchat', id: e.nextBusiest.moderatorId, name: null }} onOpen={onOpenSubject} />{' '}
                <span className="whitespace-nowrap">
                  with <span className="font-mono">{e.nextBusiest.actions}</span>
                </span>
              </>
            ) : (
              'nobody else did anything'
            )
          }
        />
      )}
      {e.teamUsualPerDay !== undefined && (
        <Row label="Team’s usual per moderator-day" value={fixed(e.teamUsualPerDay)} mono />
      )}
      {e.ownUsualPerDay !== undefined && (
        <Row
          label="Their usual"
          value={
            e.ownUsualPerDay === null ? (
              'none yet'
            ) : (
              <>
                <span className="font-mono">{fixed(e.ownUsualPerDay)}</span> a day over{' '}
                <span className="font-mono">{e.ownActiveDays ?? '?'}</span> active days
              </>
            )
          }
        />
      )}
      <Row
        label="Rule applied"
        value={Object.entries(e.threshold).map(([k, v], i) => (
          <span key={k}>
            {i > 0 && ' · '}
            {THRESHOLD_WORDS[k] ?? k} <span className="font-mono">{fixed(v)}</span>
          </span>
        ))}
      />
    </Facts>
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
  // No wider than the fact's value, so a long id on a phone ends in an ellipsis inside the row.
  return <SubjectLink id={person.id} name={person.name} onOpen={onOpen} className="max-w-full text-foreground" />
}

/** The evidence's facts, one `Row` each, with the values in the text colour and the labels muted. */
function Facts({ children }: { children: React.ReactNode }) {
  return <div className="mt-2 max-w-lg text-foreground">{children}</div>
}
