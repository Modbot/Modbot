import { useCallback, useEffect, useState } from 'react'
import { AuditLogList } from '@/components/audit/AuditLogList'
import { EditProfileDialog } from '@/components/bluesky/EditProfileDialog'
import { Empty } from '@/components/ListParts'
import { Pager } from '@/components/Pager'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Notice } from '@/components/ui/notice'
import { Tabs } from '@/components/ui/tabs'
import { ApiError, api, type BlueskyProfile, type BlueskyProfilePosts, type CurrentUser } from '@/lib/api'
import { dateTime } from '@/lib/format'
import { settingsPath } from '@/lib/integrations'
import { usePageState } from '@/lib/listPage'
import type { FilterChip } from '@/lib/filters'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { PostNumbers } from '@/pages/Marketing'

/**
 * The Bluesky page: the group's account laid out the way Bluesky draws a profile (banner, round
 * picture over its edge, name, handle, the three numbers, bio) with Modbot's own parts and colours,
 * then Posts (what Modbot sent, with their numbers) and Replies (the replies, mentions and quotes
 * kept in the audit log).
 *
 * The profile is read from Bluesky's public API by the server, so no sign-in is needed to see it;
 * Edit profile (Manage settings) is the one thing that needs Modbot signed in as the account.
 */
export function BlueskyPage({ me }: { me: CurrentUser }) {
  const [profile, setProfile] = useState<BlueskyProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)

  const load = useCallback(
    () =>
      api
        .blueskyProfile()
        .then((read) => {
          setProfile(read)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the Bluesky profile.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error) return <Empty tone="danger" onTryAgain={load}>{error}</Empty>
  if (!profile) return <Empty tone="loading" />

  if (profile.state === 'notSetUp') {
    const href = settingsPath('bluesky')

    return (
      <Empty>
        Bluesky is not set up.{' '}
        <a href={href} onClick={followLink(href)} className="text-link underline-offset-4 hover:underline">
          Settings
        </a>
      </Empty>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      {profile.state === 'unavailable' && profile.problem && <Notice tone="danger" title={profile.problem} />}
      {profile.saveError && (
        <Notice tone="danger" title="Profile not saved">
          {profile.saveError}
          {profile.saveErrorAt && <> ({dateTime(profile.saveErrorAt)})</>}
        </Notice>
      )}

      <Card className="overflow-hidden">
        <ProfileTop profile={profile} onEdit={() => setEditing(true)} />
        <ProfileTabs me={me} />
      </Card>

      {profile.canEdit && (
        <EditProfileDialog open={editing} onOpenChange={setEditing} profile={profile} onSaved={setProfile} />
      )}
    </div>
  )
}

/**
 * Banner, round picture, name, handle, numbers and bio. A number Bluesky did not give is left out
 * rather than shown as 0, and a picture the account has not set is a plain block in the card's colours.
 */
function ProfileTop({ profile, onEdit }: { profile: BlueskyProfile; onEdit: () => void }) {
  const numbers = [
    { n: profile.followers, label: 'followers' },
    { n: profile.following, label: 'following' },
    { n: profile.posts, label: 'posts' },
  ].filter((x): x is { n: number; label: string } => x.n !== null)

  const signIn = settingsPath('bluesky')

  return (
    <>
      {profile.bannerUrl ? (
        <img
          src={profile.bannerUrl}
          alt=""
          referrerPolicy="no-referrer"
          className="aspect-[3/1] max-h-64 w-full border-b border-b-(length:--hairline) bg-muted object-cover"
        />
      ) : (
        <div className="aspect-[3/1] max-h-64 w-full border-b border-b-(length:--hairline) bg-strip" />
      )}

      <div className="flex flex-col gap-2 px-(--panel-pad) pb-(--panel-pad)">
        <div className="flex items-end justify-between gap-3">
          {/* Cut out of the card by a ring of the card's own colour, over the banner's edge. */}
          {profile.avatarUrl ? (
            <img
              src={profile.avatarUrl}
              alt=""
              referrerPolicy="no-referrer"
              className="-mt-10 size-20 shrink-0 rounded-full bg-muted object-cover ring-4 ring-card sm:-mt-12 sm:size-24"
            />
          ) : (
            <div className="-mt-10 size-20 shrink-0 rounded-full bg-muted ring-4 ring-card sm:-mt-12 sm:size-24" />
          )}

          {profile.canEdit && profile.state === 'ready' && (
            <div className="pt-3">
              {profile.signedIn ? (
                <Button variant="outline" size="sm" onClick={onEdit}>
                  Edit profile
                </Button>
              ) : (
                <Button asChild variant="outline" size="sm">
                  <a href={signIn} onClick={followLink(signIn)}>
                    Sign in
                  </a>
                </Button>
              )}
            </div>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-0.5">
          <h2 className="font-display leading-tight break-words" style={{ fontSize: 'calc(var(--text-base) * 1.85)' }}>
            {profile.displayName || profile.handle}
          </h2>
          {profile.handle && <span className="break-all text-muted-foreground">@{profile.handle}</span>}
        </div>

        {numbers.length > 0 && (
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {numbers.map(({ n, label }) => (
              <span key={label}>
                <span className="font-mono text-foreground">{n.toLocaleString()}</span> {label}
              </span>
            ))}
          </div>
        )}

        {profile.bio && <p className="break-words whitespace-pre-wrap">{profile.bio}</p>}
      </div>
    </>
  )
}

type TabId = 'posts' | 'replies'

/** Posts for somebody who may read posts, Replies for somebody who may read the audit log. */
function ProfileTabs({ me }: { me: CurrentUser }) {
  const tabs: { value: TabId; label: string }[] = []
  if (can(me, 'ViewPosts')) tabs.push({ value: 'posts', label: 'Posts' })
  if (can(me, 'ViewAuditLog')) tabs.push({ value: 'replies', label: 'Replies' })

  const [tab, setTab] = useState<TabId | null>(null)
  const shown = tab && tabs.some((t) => t.value === tab) ? tab : tabs[0]?.value

  if (!shown) return null

  return (
    <Tabs value={shown} onChange={setTab} tabs={tabs} rowClassName="border-t border-t-(length:--hairline) px-1" panelClassName="overflow-visible">
      {shown === 'posts' ? <ProfilePosts /> : <ProfileReplies />}
    </Tabs>
  )
}

/** What Modbot sent to Bluesky, newest first, with the numbers under each. */
function ProfilePosts() {
  const at = usePageState()
  const [read, setRead] = useState<BlueskyProfilePosts | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .blueskyProfilePosts(at.page)
      .then((next) => {
        if (cancelled) return
        setRead(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not load the posts.')
      })

    return () => {
      cancelled = true
    }
  }, [at.page, tries])

  if (error) return <Empty tone="danger" onTryAgain={() => setTries((n) => n + 1)}>{error}</Empty>
  if (!read) return <Empty tone="loading" />
  if (read.posts.length === 0) return <Empty>No posts sent.</Empty>

  return (
    <>
      <ul>
        {read.posts.map((post) => (
          <li
            key={post.postId}
            className="flex flex-col gap-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-3 last:border-b-0"
          >
            {post.title && <span className="font-medium break-words">{post.title}</span>}
            {post.text && <p className="break-words whitespace-pre-wrap">{post.text}</p>}
            {post.link && (
              <a
                href={post.link}
                target="_blank"
                rel="noreferrer"
                className="break-all text-link underline-offset-4 hover:underline"
                style={{ fontSize: 'var(--text-small)' }}
              >
                {post.link}
              </a>
            )}
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
              {post.postedAt && <span className="font-mono text-muted-foreground">{dateTime(post.postedAt)}</span>}
              {post.counts && <PostNumbers counts={post.counts} />}
            </div>
          </li>
        ))}
      </ul>
      <Pager at={at} pages={Math.ceil(read.total / read.pageSize)} />
    </>
  )
}

/** The replies, mentions and quotes of the account, from the audit log's own list. */
const REPLY_CHIPS: FilterChip[] = [
  { property: 'type', operator: 'is', values: ['modbot.bluesky.reply', 'modbot.bluesky.mention', 'modbot.bluesky.quote'] },
]
const NO_CHIPS: FilterChip[] = []
const REPLY_STARTS = [NO_CHIPS]

function ProfileReplies() {
  const [chips, setChips] = useState(NO_CHIPS)

  return <AuditLogList chips={chips} starts={REPLY_STARTS} onChange={setChips} fixed={REPLY_CHIPS} place="popup" />
}
