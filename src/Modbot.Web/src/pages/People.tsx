import { useMemo } from 'react'
import { PeopleBrowser } from '@/components/people/PeopleBrowser'
import { type CurrentUser } from '@/lib/api'
import { useFilters } from '@/lib/filters'
import { useListPage } from '@/lib/listPage'
import { MEMBERS_VIEW, memberView, PEOPLE_DEFAULTS } from '@/lib/pageFilters'
import { can } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'

/**
 * Everyone Modbot has a record of, and the group's member list.
 *
 * A row for every VRChat account Modbot has ever seen anywhere -- somebody who walked through one
 * instance, an account named once in the audit log, a moderator from another group. Until this
 * page the only way to reach one of those people was to already know their id.
 *
 * The member list is this page with the Membership chip on Members (2026-09-27). It was a page of
 * its own over the same people, with its own filters and columns, and two lists of one set of
 * people meant two places to look. Narrowed to members or to people who left, the table takes
 * the member list's columns -- Discord, roles, when they joined, when they left -- and its kick
 * and ban menu; narrowed any other way it keeps People's own. Every filter from both is in the
 * one bar.
 *
 * The list itself is `PeopleBrowser`, which an instance's popup uses too. This page keeps what is
 * its own: the chips in the address, and the stretch an alert links to.
 */

/** The filters somebody with See members and not See profiles may use: the member list's own. */
const MEMBER_LIST_FILTERS = new Set(['membership', 'role', 'hasRole', 'banned', 'everBanned', 'trustRank', 'platform', 'eighteenPlus', 'representing', 'joined', 'seen', 'profile'])

export function People({ me }: { me: CurrentUser }) {
  // See members alone opens the member list's two views and nothing wider; the server refuses the
  // rest regardless. Links need See profiles (Discord account linking design §11).
  const seesProfiles = can(me, 'ViewProfile')
  const at = useListPage()

  // The chips: in the address (lib/filters.ts). Nothing narrowed by default; every link to
  // "Members" carries its chip in the address.
  const [said, setChips] = useFilters(PEOPLE_DEFAULTS)

  // What the list is narrowed by. For See members alone, only the member list's filters, and
  // always one of its two views: current members unless the chips ask for people who left.
  const chips = useMemo(() => {
    if (seesProfiles) return said
    const allowed = said.filter((c) => MEMBER_LIST_FILTERS.has(c.property))
    return memberView(allowed) ? allowed : [...allowed.filter((c) => c.property !== 'membership'), ...MEMBERS_VIEW]
  }, [said, seesProfiles])

  // Where the page starts, and where Clear goes back to: Members when the list is Members, else People.
  const starts = memberView(chips) === 'member' ? [MEMBERS_VIEW, PEOPLE_DEFAULTS] : [PEOPLE_DEFAULTS, MEMBERS_VIEW]

  // The stretch an unusual-activity alert links to. In the address bar rather than in state, so
  // the link a moderator was sent lands on the same list they were meant to see. Hour-precise, so
  // not a Joined chip, which is whole days.
  const [joinedFrom, setJoinedFrom] = useQueryParam('joinedFrom')
  const [joinedTo, setJoinedTo] = useQueryParam('joinedTo')
  const joined = joinedFrom && joinedTo ? { from: joinedFrom, to: joinedTo } : null

  return (
    <PeopleBrowser
      me={me}
      chips={chips}
      starts={starts}
      onChange={setChips}
      at={at}
      place="page"
      joined={joined}
      onClearJoined={() => {
        setJoinedFrom(null)
        setJoinedTo(null)
        at.restart()
      }}
    />
  )
}
