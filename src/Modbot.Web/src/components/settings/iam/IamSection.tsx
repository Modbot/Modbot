import { useEffect, useMemo, useState } from 'react'
import { Tabs } from '@/components/ui/tabs'
import type { CurrentUser } from '@/lib/api'
import { can } from '@/lib/permissions'
import { Roles } from '@/pages/Roles'
import { Users } from '@/pages/Users'

/**
 * Settings → IAM: Modbot's own accounts, and the roles that say what each may do.
 *
 * Both were pages of their own in the sidebar until 2026-09-18. They are settings of the
 * deployment rather than things a moderator works in, and the sidebar is for the latter, so the
 * maintainer moved them here. `/users` and `/roles` still lead to the half they named.
 *
 * Their two permissions are not Manage settings, so each half is drawn only for somebody who
 * holds its own, and the tab itself is drawn only when they hold at least one (see Settings.tsx).
 *
 * The name is the maintainer's, asked for directly on 2026-09-18. It is an initialism a volunteer
 * moderator would have to be taught, which the naming rule in CLAUDE.md otherwise rejects; it is
 * recorded here and in the spec so nobody later "fixes" it.
 */
const IAM_TABS = [
  { value: 'users', label: 'Users', needs: 'ManageUsers' },
  { value: 'roles', label: 'Roles', needs: 'ManageRoles' },
] as const

type IamTabId = (typeof IAM_TABS)[number]['value']

/** The part after the slash in `/settings#iam/roles`. */
function subTabFromHash(open: readonly IamTabId[]): IamTabId {
  const wanted = window.location.hash.slice(1).split('/')[1]
  return open.find((t) => t === wanted) ?? open[0]
}

export function IamSection({ me }: { me: CurrentUser }) {
  const open = useMemo(
    () => IAM_TABS.filter((t) => can(me, t.needs)).map((t) => t.value),
    [me],
  )

  const [tab, setTab] = useState<IamTabId | null>(() => (open.length === 0 ? null : subTabFromHash(open)))

  useEffect(() => {
    if (open.length === 0) return

    const onHash = () => setTab(subTabFromHash(open))
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [open])

  if (tab === null) return null

  const choose = (next: IamTabId) => {
    setTab(next)
    window.history.replaceState(window.history.state, '', `#iam/${next}`)
  }

  return (
    <Tabs
      value={tab}
      onChange={choose}
      tabs={IAM_TABS.filter((t) => open.includes(t.value)).map(({ value, label }) => ({ value, label }))}
      className="gap-3"
    >
      {tab === 'users' && <Users me={me} />}
      {tab === 'roles' && <Roles me={me} />}
    </Tabs>
  )
}
