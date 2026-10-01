import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { SwitchBank } from '@/components/ui/switch-bank'
import { EmptyRow } from '@/components/PanelGrid'
import { More, Panel } from '@/components/subject/shared'
import { moderationApi, targetLabel, type ModerationFlag } from '@/lib/autoMod'
import { ago, lengthOfTime } from '@/lib/format'
import { go } from '@/lib/router'
import { useLoad } from '@/lib/useLoad'

type State = 'open' | 'dismissed' | 'confirmed'

/**
 * What AutoMod rules flagged about this person, under either of their accounts.
 *
 * Read-only. Dismissing and opening a review stay on the Flags page, where a moderator sees the
 * flag beside every other one and the AI's opinion; the popup answers "has this person been
 * flagged", which it could not answer at all before (UX review 2026-09-25, finding 1).
 */
export function PersonFlags({ vrchatId, discordId }: { vrchatId: string | null; discordId: string | null }) {
  const [state, setState] = useState<State>('open')

  const load = useCallback(
    () => moderationApi.personFlags(state, { vrchat: vrchatId, discord: discordId }),
    [state, vrchatId, discordId],
  )
  const { data, error, reload } = useLoad(load)

  return (
    <div className="flex min-h-0 flex-col">
      <div className="shrink-0 border-b border-b-(length:--hairline) px-(--panel-pad) py-2">
        <SwitchBank
          value={state}
          onChange={setState}
          options={[
            { value: 'open', label: 'Open' },
            { value: 'dismissed', label: 'Dismissed' },
            { value: 'confirmed', label: 'Confirmed' },
          ]}
        />
      </div>

      <Panel title="Flags" right={<More onClick={() => go('/flags')}>Flags page</More>} flush>
        {error && <EmptyRow tone="danger" onTryAgain={reload}>{error}</EmptyRow>}
        {!error && !data && <EmptyRow tone="loading" />}
        {data && data.flags.length === 0 && <EmptyRow>No flags.</EmptyRow>}
        {data && data.flags.length > 0 && (
          <ol className="flex flex-col">
            {data.flags.map((flag) => (
              <FlagRow key={flag.id} flag={flag} />
            ))}
          </ol>
        )}
      </Panel>
    </div>
  )
}

function FlagRow({ flag }: { flag: ModerationFlag }) {
  const now = new Date().toISOString()

  return (
    <li
      className="border-t border-t-(length:--hairline) px-(--panel-pad) py-2 first:border-t-0"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{flag.ruleName}</span>
        <Badge variant="outline">{flag.subjectPlatform === 'discord' ? 'Discord' : 'VRChat'}</Badge>
        <Badge variant="outline">{targetLabel(flag.target)}</Badge>
        {flag.messageDeleted && <Badge variant="destructive">Message deleted</Badge>}
        {flag.timedOutMinutes && <Badge variant="destructive">Timed out {lengthOfTime(flag.timedOutMinutes)}</Badge>}
        {flag.groupBanned && <Badge variant="destructive">Banned from the group</Badge>}
        {flag.groupRemoved && <Badge variant="destructive">Removed from the group</Badge>}
        {flag.trial && <Badge variant="secondary">Trial</Badge>}
        {flag.reviewId && <Badge variant="secondary">In review</Badge>}
        <span className="flex-1" />
        <span className="text-muted-foreground">{ago(flag.flaggedAt, now)}</span>
      </div>
      <div className="mt-1 break-words">
        {flag.picture ? flag.picture : `“${flag.matched}”`}
        {flag.ruleKind === 'termList' && <span className="ml-2 font-mono text-muted-foreground">{flag.term}</span>}
      </div>
      {flag.reason && <div className="mt-0.5 text-muted-foreground">{flag.reason}</div>}
      {flag.state === 'dismissed' && (
        <div className="mt-0.5 text-muted-foreground">
          Dismissed by {flag.dismissedBy ?? 'someone'} {ago(flag.dismissedAt, now)}
        </div>
      )}
      {flag.state === 'confirmed' && (
        <div className="mt-0.5 text-muted-foreground">
          Confirmed by {flag.confirmedBy ?? 'someone'} {ago(flag.confirmedAt, now)}
        </div>
      )}
    </li>
  )
}
