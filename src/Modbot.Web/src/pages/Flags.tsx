import { useCallback, useEffect, useState } from 'react'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs } from '@/components/ui/tabs'
import { moderationApi, targetLabel, type ModerationFlag } from '@/lib/aiModeration'
import { ApiError, type CurrentUser } from '@/lib/api'
import { ago } from '@/lib/format'
import { can } from '@/lib/permissions'
import { cn } from '@/lib/utils'

/**
 * What AI moderation rules flagged (AI moderation design §5). Seeing needs ViewProfile; the
 * Dismiss button needs ReviewTickets, and a dismissal is permanent for that rule and that person.
 */
export function Flags({ me, onOpenSubject }: { me: CurrentUser; onOpenSubject: (id: string) => void }) {
  const [state, setState] = useState<'open' | 'dismissed'>('open')
  const [flags, setFlags] = useState<ModerationFlag[] | null>(null)
  const [open, setOpen] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const mayDismiss = can(me, 'ReviewTickets')

  const load = useCallback(() => {
    moderationApi
      .flags(state)
      .then((r) => {
        setFlags(r.flags)
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
  }, [state])

  useEffect(() => {
    load()
  }, [load])

  const dismiss = (id: string) => {
    setBusy(id)
    setProblem(null)
    moderationApi
      .dismissFlag(id)
      .then(load)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not dismiss the flag.'))
      .finally(() => setBusy(null))
  }

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  const now = new Date().toISOString()

  return (
    <Tabs
      value={state}
      onChange={setState}
      tabs={[
        { value: 'open', label: 'Open', badge: open },
        { value: 'dismissed', label: 'Dismissed' },
      ]}
      className="gap-4"
    >
      {problem && <div className="text-destructive">{problem}</div>}
      <Card>
        <CardContent className="p-0">
          {!flags ? (
            <div className="py-10 text-center text-muted-foreground">Loading…</div>
          ) : flags.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">No flags</div>
          ) : (
            <ul className="flex flex-col">
              {flags.map((flag) => (
                <li
                  key={flag.id}
                  className={cn(
                    'flex flex-wrap items-start gap-x-4 gap-y-1 border-b px-4 py-3 last:border-0',
                  )}
                  style={{
                    borderBottomWidth: 'var(--hairline)',
                    fontSize: 'var(--text-small)',
                  }}
                >
                  <div className="min-w-0 flex-1">
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
                      <span className="font-medium">{flag.ruleName}</span>
                      {flag.messageDeleted && <Badge variant="destructive">Message deleted</Badge>}
                      {flag.timedOutMinutes && (
                        <Badge variant="destructive">Timed out {flag.timedOutMinutes} min</Badge>
                      )}
                      {flag.trial && (
                        <Badge variant="secondary">
                          {[
                            'Trial',
                            flag.wouldDeleteMessage ? 'would delete' : null,
                            flag.wouldTimeOutMinutes ? `would time out ${flag.wouldTimeOutMinutes} min` : null,
                          ]
                            .filter(Boolean)
                            .join(' · ')}
                        </Badge>
                      )}
                    </div>
                    <div className="mt-1">
                      “{flag.matched}”
                      {flag.ruleKind === 'termList' && (
                        <span className="ml-2 font-mono text-muted-foreground">{flag.term}</span>
                      )}
                    </div>
                    {flag.reason && <div className="mt-0.5 text-muted-foreground">{flag.reason}</div>}
                    {flag.ruleText && (
                      <div className="mt-0.5 break-words text-muted-foreground">
                        {flag.ruleName} v{flag.ruleVersion}: {flag.ruleText}
                      </div>
                    )}
                    <div className="mt-0.5 text-muted-foreground">
                      {ago(flag.flaggedAt, now)}
                      {flag.state === 'dismissed' &&
                        ` · Dismissed by ${flag.dismissedBy ?? 'someone'} ${ago(flag.dismissedAt, now)}`}
                    </div>
                  </div>
                  {mayDismiss && flag.state === 'open' && (
                    <Button
                      size="xs"
                      variant="outline"
                      disabled={busy !== null}
                      onClick={() => dismiss(flag.id)}
                    >
                      {busy === flag.id ? 'Dismissing…' : 'Dismiss'}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </Tabs>
  )
}
