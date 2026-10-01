import { Fragment, useCallback } from 'react'
import { PersonLink } from '@/components/facts'
import { api } from '@/lib/api'
import { hasStory, joinStory, JOIN_STORY_TYPES, times, type Who } from '@/lib/joinStory'
import { useLoad } from '@/lib/useLoad'

/** How many facts the story is read from: the log's own most per page, which no one person's joining comes near. */
const READ = 200

/**
 * Who invited this person, who let them in, and how often they asked and were turned down, as
 * lines in the Membership card.
 *
 * Read from the facts about them rather than stored anywhere: VRChat's member list has no column
 * for who let somebody in, and the invite, the request and the decision are each an audit log
 * entry already. So it needs the audit log's own permission, and the card leaves it out for anybody
 * without it.
 */
export function JoinStory({ vrchatId, version = 0 }: { vrchatId: string; version?: number | string }) {
  const load = useCallback(
    () => api.audit({ subject: vrchatId, subjectPlatform: 'VRChat', type: JOIN_STORY_TYPES, limit: READ }),
    [vrchatId],
  )
  const { data } = useLoad(load, version)

  if (!data) return null

  const story = joinStory(data.entries, vrchatId)
  if (!hasStory(story)) return null

  return (
    <>
      {story.invitedBy && (
        <p>
          Invited by <Name who={story.invitedBy} />.
        </p>
      )}
      {story.approvedBy && (
        <p>
          Approved by <Name who={story.approvedBy} />.
        </p>
      )}
      {story.asked > 0 && (
        <p>
          Asked to join {times(story.asked)}
          {story.rejected.count > 0 && (
            <>
              , rejected {times(story.rejected.count)}
              {story.rejected.by.length > 0 && (
                <>
                  {' '}by <Names who={story.rejected.by} />
                </>
              )}
            </>
          )}
          .
        </p>
      )}
      {story.asked === 0 && story.rejected.count > 0 && (
        <p>
          Rejected {times(story.rejected.count)}
          {story.rejected.by.length > 0 && (
            <>
              {' '}by <Names who={story.rejected.by} />
            </>
          )}
          .
        </p>
      )}
      {story.blocked > 0 && <p className="text-destructive">Blocked from asking.</p>}
    </>
  )
}

function Name({ who }: { who: Who }) {
  if (!who.id) return <>{who.name ?? 'Modbot'}</>
  return <PersonLink platform={who.platform} id={who.id} name={who.name} />
}

/** "Ada", "Ada and Ben", "Ada, Ben and Cy". */
function Names({ who }: { who: Who[] }) {
  return (
    <>
      {who.map((w, i) => (
        <Fragment key={`${w.platform}:${w.id}`}>
          {i > 0 && (i === who.length - 1 ? ' and ' : ', ')}
          <Name who={w} />
        </Fragment>
      ))}
    </>
  )
}
