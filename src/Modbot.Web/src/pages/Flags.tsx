import { useCallback, useEffect, useState } from 'react'
import { changesFlags } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { SwitchBank } from '@/components/ui/switch-bank'
import {
  moderationApi,
  PROPOSED_ACTION_LABELS,
  targetLabel,
  UNKNOWN_LANGUAGE,
  type FlagLanguageCount,
  type ModerationFlag,
} from '@/lib/autoMod'
import { ApiError, type CurrentUser } from '@/lib/api'
import { ago } from '@/lib/format'
import { can } from '@/lib/permissions'

/**
 * What AutoMod rules flagged (AI moderation design §5). Seeing needs ViewProfile; the Dismiss,
 * Review and Ask AI buttons need ReviewTickets, and a dismissal is permanent for that rule and
 * that person. The AI's opinion is advice: it sits beside the words and does nothing by itself.
 */
export function Flags({ me, onOpenSubject }: { me: CurrentUser; onOpenSubject: (id: string) => void }) {
  const [state, setState] = useState<'open' | 'dismissed' | 'confirmed'>('open')
  const [language, setLanguage] = useState<string | null>(null)
  const [flags, setFlags] = useState<ModerationFlag[] | null>(null)
  const [languages, setLanguages] = useState<FlagLanguageCount[]>([])
  const [aiOpinionAvailable, setAiOpinionAvailable] = useState(false)
  const [open, setOpen] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const mayDismiss = can(me, 'ReviewTickets')

  const load = useCallback(() => {
    moderationApi
      .flags(state, language)
      .then((r) => {
        setFlags(r.flags)
        setLanguages(r.languages)
        setAiOpinionAvailable(r.aiOpinionAvailable)
        setOpen(r.open)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see flags.'
            : 'Could not load the flags.',
        ),
      )
  }, [state, language])

  // And again when the live stream says a rule raised or settled a flag.
  const live = useLiveVersion(changesFlags)

  useEffect(() => {
    load()
  }, [load, live])

  const act = (id: string, work: () => Promise<unknown>, fallback: string) => {
    setBusy(id)
    setProblem(null)
    work()
      .then(load)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : fallback))
      .finally(() => setBusy(null))
  }

  if (error) {
    return (
      <Card>
        <EmptyRow tone="danger">{error}</EmptyRow>
      </Card>
    )
  }

  const now = new Date().toISOString()

  return (
    <div className="flex flex-col gap-3">
      <SwitchBank
        value={state}
        onChange={setState}
        options={[
          {
            value: 'open',
            label:
              open > 0 ? (
                <>
                  Open <span className="font-mono">{open}</span>
                </>
              ) : (
                'Open'
              ),
          },
          { value: 'dismissed', label: 'Dismissed' },
          { value: 'confirmed', label: 'Confirmed' },
        ]}
      />
      {languages.length > 1 && (
        <SwitchBank
          value={language ?? ''}
          onChange={(next) => setLanguage(next === '' ? null : next)}
          options={[
            { value: '', label: 'All languages' },
            ...languages.map((l) => ({
              value: l.language ?? UNKNOWN_LANGUAGE,
              label: (
                <>
                  {l.label} <span className="font-mono">{l.flags}</span>
                </>
              ),
            })),
          ]}
        />
      )}
      {problem && <div className="text-destructive">{problem}</div>}
      <Card>
        {!flags ? (
          <EmptyRow>Loading…</EmptyRow>
        ) : flags.length === 0 ? (
          <EmptyRow>No flags</EmptyRow>
        ) : (
          <ul className="flex flex-col divide-y-(--hairline) divide-border">
            {flags.map((flag) => (
              <li
                key={flag.id}
                className="flex flex-wrap items-start gap-x-4 gap-y-1 px-(--panel-pad) py-2"
                style={{ fontSize: 'var(--text-small)' }}
              >
                <div className="min-w-64 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    {flag.subjectPlatform === 'vrchat' ? (
                      <SubjectLink id={flag.subjectId} name={flag.subjectName} onOpen={onOpenSubject} />
                    ) : (
                      <DiscordPersonLink id={flag.subjectId} name={flag.subjectName} />
                    )}
                    <Badge variant="outline">
                      {flag.subjectPlatform === 'discord' ? 'Discord' : 'VRChat'}
                    </Badge>
                    <Badge variant="outline">{targetLabel(flag.target)}</Badge>
                    <Badge variant="outline">{flag.languageLabel}</Badge>
                    {flag.picture && <Badge variant="outline">Picture</Badge>}
                    <span className="font-medium">{flag.ruleName}</span>
                    {flag.messageDeleted && <Badge variant="destructive">Message deleted</Badge>}
                    {flag.timedOutMinutes && (
                      <Badge variant="destructive">Timed out {flag.timedOutMinutes} min</Badge>
                    )}
                    {flag.groupBanned && <Badge variant="destructive">Banned from the group</Badge>}
                    {flag.groupRemoved && <Badge variant="destructive">Removed from the group</Badge>}
                    {flag.trial && (
                      <Badge variant="secondary">
                        {[
                          'Trial',
                          flag.wouldDeleteMessage ? 'would delete' : null,
                          flag.wouldTimeOutMinutes ? `would time out ${flag.wouldTimeOutMinutes} min` : null,
                          flag.wouldGroupBan ? 'would ban from the group' : null,
                          flag.wouldGroupRemove ? 'would remove from the group' : null,
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                      </Badge>
                    )}
                    {flag.reviewId && <Badge variant="secondary">In review</Badge>}
                  </div>
                  {flag.context && flag.context.length > 0 && (
                    <ul className="mt-1 flex flex-col text-muted-foreground">
                      {flag.context.map((m) => (
                        <li key={m.messageId} className="truncate">
                          {m.author}: {m.text}
                        </li>
                      ))}
                    </ul>
                  )}
                  <div className="mt-1">
                    {flag.picture ? (
                      flag.pictureUrl ? (
                        <a
                          href={flag.pictureUrl}
                          target="_blank"
                          rel="noreferrer noopener"
                          className="underline"
                        >
                          {flag.picture}
                        </a>
                      ) : (
                        flag.picture
                      )
                    ) : (
                      `“${flag.matched}”`
                    )}
                    {flag.ruleKind === 'termList' && (
                      <span className="ml-2 font-mono text-muted-foreground">{flag.term}</span>
                    )}
                  </div>
                  {flag.reason && <div className="mt-0.5 text-muted-foreground">{flag.reason}</div>}
                  {flag.aiOpinion && (
                    <div className="mt-0.5 flex flex-wrap items-center gap-2">
                      <Badge variant="outline">AI: {flag.aiOpinion === 'keep' ? 'Keep' : 'Dismiss'}</Badge>
                      {flag.aiProposedAction && flag.aiProposedAction !== 'none' && (
                        <Badge variant="outline">Proposed: {PROPOSED_ACTION_LABELS[flag.aiProposedAction]}</Badge>
                      )}
                      <span className="text-muted-foreground">{flag.aiOpinionReason}</span>
                    </div>
                  )}
                  {flag.ruleText && (
                    <div className="mt-0.5 break-words text-muted-foreground">
                      {flag.ruleName} v{flag.ruleVersion}: {flag.ruleText}
                    </div>
                  )}
                  <div className="mt-0.5 text-muted-foreground">
                    {ago(flag.flaggedAt, now)}
                    {flag.state === 'dismissed' &&
                      ` · Dismissed by ${flag.dismissedBy ?? 'someone'} ${ago(flag.dismissedAt, now)}`}
                    {flag.state === 'confirmed' &&
                      ` · Confirmed by ${flag.confirmedBy ?? 'someone'} ${ago(flag.confirmedAt, now)}`}
                    {flag.callId && (
                      <>
                        {' · '}
                        <a className="underline" href={`/settings#ai/calls/${flag.callId}`}>
                          AI call
                        </a>
                      </>
                    )}
                  </div>
                </div>
                {mayDismiss && flag.state === 'open' && (
                  <div className="flex items-center gap-2">
                    {aiOpinionAvailable && (
                      <Button
                        size="xs"
                        variant="outline"
                        disabled={busy !== null}
                        onClick={() =>
                          act(flag.id, () => moderationApi.askAiAboutFlag(flag.id), 'Could not ask the AI.')
                        }
                      >
                        {busy === flag.id ? 'Asking…' : 'Ask AI'}
                      </Button>
                    )}
                    {!flag.reviewId && (
                      <Button
                        size="xs"
                        variant="outline"
                        disabled={busy !== null}
                        onClick={() =>
                          act(
                            flag.id,
                            () => moderationApi.openFlagReview(flag.id),
                            'Could not open a review.',
                          )
                        }
                      >
                        {busy === flag.id ? 'Working…' : 'Open a review'}
                      </Button>
                    )}
                    <Button
                      size="xs"
                      variant="outline"
                      disabled={busy !== null}
                      onClick={() =>
                        act(flag.id, () => moderationApi.dismissFlag(flag.id), 'Could not dismiss the flag.')
                      }
                    >
                      Dismiss
                    </Button>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  )
}
