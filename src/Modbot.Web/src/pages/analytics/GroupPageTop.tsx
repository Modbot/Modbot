import type { CurrentUser } from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { useGroupInfo } from '@/lib/useGroupInfo'
import { GroupHeader } from './GroupHeader'
import { SettingsTabs } from './GroupSettings'

/**
 * The group's header over a Modbot page that one of its tabs leads out to (Events, Members, Banned
 * Users, Logs), drawn only when the page was opened from that row (`groupTabFrom`), so the person has
 * not left the group's page. Logs is inside Settings, so its row of Settings tabs comes too.
 *
 * Read once from what Modbot stored and left out if it cannot be read, so the page below never
 * waits on it.
 */
export function GroupPageTop({
  me,
  page,
  tab,
  pathOf,
}: {
  me: CurrentUser
  page: PageId
  /** The tab of the row to mark. */
  tab: PageId
  pathOf: (id: PageId) => string
}) {
  const { info } = useGroupInfo()
  if (!info) return null

  return (
    <div className="mb-3 flex flex-col gap-3">
      <GroupHeader info={info} me={me} pathOf={pathOf} active={tab} />
      {page === 'audit' && <SettingsTabs me={me} pathOf={pathOf} active="audit" />}
    </div>
  )
}
