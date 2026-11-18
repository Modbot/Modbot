import { BanReasonsCard } from './BanReasonsCard'
import { FlaggedCard } from './FlaggedCard'
import { ProblemAvatarsCard } from './ProblemAvatarsCard'
import { RepeatOffendersCard } from './RepeatOffendersCard'
import { SettingsSection } from './SettingsCard'

/**
 * Settings → Moderation: the things that shape what a moderator is asked for at the moment they
 * act, rather than how the deployment is run.
 *
 * The reason list (with the actions each reason is offered on, and whether a reason is required
 * beyond bans), the rules the repeat-offender status is decided by, and the rules that make
 * somebody Flagged, and the problem avatars that flag whoever wears one.
 */
export function ModerationSection() {
  return (
    <SettingsSection id="moderation" title="Moderation">
      <BanReasonsCard />
      <RepeatOffendersCard />
      <FlaggedCard />
      <ProblemAvatarsCard />
    </SettingsSection>
  )
}
