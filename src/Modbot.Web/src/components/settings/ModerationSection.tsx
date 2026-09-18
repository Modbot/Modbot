import { BanReasonsCard } from './BanReasonsCard'
import { RepeatOffendersCard } from './RepeatOffendersCard'
import { SettingsSection } from './SettingsCard'

/**
 * Settings → Moderation: the things that shape what a moderator is asked for at the moment they
 * act, rather than how the deployment is run.
 *
 * The ban reason list, and the rules the repeat-offender status is decided by. The optional
 * classification on kicks and warns, and whether it is required, belong here when those actions
 * exist.
 */
export function ModerationSection() {
  return (
    <SettingsSection id="moderation" title="Moderation">
      <BanReasonsCard />
      <RepeatOffendersCard />
    </SettingsSection>
  )
}
