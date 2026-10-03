import { useCallback, useEffect, useState } from 'react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { PostComposer } from '@/components/marketing/PostComposer'
import { Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Tabs } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { dateTimeWithWeekday } from '@/lib/format'
import { changesPosts } from '@/lib/liveRules'
import type { PageId } from '@/lib/nav'
import {
  blankPost,
  canCancel,
  canEditWhole,
  canPostNow,
  discordMessage,
  duplicateOf,
  headline,
  inputFrom,
  LIST_LABEL,
  listAfterSave,
  NETWORK_LABEL,
  postsApi,
  shownLabel,
  shownTone,
  tidy,
  vrchatAudience,
  DISCORD_LIMIT,
  type Post,
  type PostDestination,
  type PostInput,
  type PostList,
  type PostListName,
} from '@/lib/posts'
import { followLink } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { cn } from '@/lib/utils'
import { vrchatPermissionLabel } from '@/lib/vrchatPermissions'
import { PageMessage } from '@/pages/analytics/shared'

export type MarketingPage = Extract<PageId, 'marketing' | 'marketing-sent' | 'marketing-drafts' | 'marketing-failed'>

type ListTab = Exclude<PostListName, 'cancelled'>

const LIST_OF: Record<MarketingPage, ListTab> = {
  marketing: 'scheduled',
  'marketing-sent': 'sent',
  'marketing-drafts': 'drafts',
  'marketing-failed': 'failed',
}

const PAGE_OF: Record<ListTab, MarketingPage> = {
  scheduled: 'marketing',
  sent: 'marketing-sent',
  drafts: 'marketing-drafts',
  failed: 'marketing-failed',
}

const TABS: ListTab[] = ['scheduled', 'sent', 'drafts', 'failed']

/** The browser's own zone, which a new post's time is picked in (decision 13). */
function browserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

type Composing = { post: Post | null; input: PostInput }

type Confirming =
  | { kind: 'cancel'; post: Post }
  | { kind: 'delete-draft'; post: Post }
  | { kind: 'delete-on-site'; post: Post; destination: PostDestination }

/**
 * Marketing (posts design §4): posts sent to Discord and the VRChat group at a time, in four lists, with where each went
 * and how. New post opens the composer; each row carries what can be done to it now. The list
 * redraws when a post's facts arrive, so Sending turns Posted without a reload.
 */
export function Marketing({ list, onList }: { list: MarketingPage; onList: (page: MarketingPage) => void }) {
  const tab = LIST_OF[list]
  const [cancelled, setCancelled] = useState(false)
  const [page, setPage] = useState(1)
  const [data, setData] = useState<PostList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [composing, setComposing] = useState<Composing | null>(null)
  const [editing, setEditing] = useState<{ post: Post; destination: PostDestination } | null>(null)
  const [confirming, setConfirming] = useState<Confirming | null>(null)
  const [rowError, setRowError] = useState<{ id: string; text: string } | null>(null)
  const version = useLiveVersion(changesPosts)
  const zone = browserZone()

  const shownList: PostListName = cancelled ? 'cancelled' : tab

  const load = useCallback(() => {
    return postsApi
      .list(shownList, page)
      .then((next) => {
        setData(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the posts.',
        ),
      )
  }, [shownList, page])

  useEffect(() => {
    void load()
  }, [load, version])

  // A post on its way changes state without a fact until it lands: read again while one is.
  const underWay = data?.posts.some((p) => p.destinations.some((d) => d.shown === 'sending')) ?? false
  useEffect(() => {
    if (!underWay) return
    const timer = setInterval(() => void load(), 15_000)
    return () => clearInterval(timer)
  }, [underWay, load])

  const canManage = data?.canManage ?? false
  const newPost = useCallback(() => setComposing({ post: null, input: blankPost(new Date(), zone) }), [zone])

  useShortcuts(canManage ? [{ label: 'New post', group: 'Page', page: true, run: newPost }] : [])

  const act = (post: Post, run: () => Promise<unknown>) => {
    setRowError(null)
    run()
      .then(() => load())
      .catch((e: unknown) => setRowError({ id: post.id, text: e instanceof ApiError ? e.message : 'Could not do that.' }))
  }

  const pickTab = (next: ListTab) => {
    setPage(1)
    setCancelled(false)
    onList(PAGE_OF[next])
  }

  if (!data) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  const pages = Math.max(1, Math.ceil(data.total / data.pageSize))

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <Chip
          on={cancelled}
          onClick={() => {
            setPage(1)
            setCancelled((c) => !c)
          }}
        >
          Cancelled
        </Chip>
        {data.sites.paused && <Badge variant="warn">Paused</Badge>}
        <div className="flex-1" />
        {canManage && <Button onClick={newPost}>New post</Button>}
      </div>

      <Tabs
        value={tab}
        onChange={pickTab}
        tabs={TABS.map((t) => ({ value: t, label: LIST_LABEL[t], badge: data.counts[t] }))}
        panelClassName="overflow-visible pt-3"
      >
        <div className="flex flex-col gap-3">
          {error && <Outcome tone="problem">{error}</Outcome>}

          {data.posts.length === 0 ? (
            <PageMessage>No posts.</PageMessage>
          ) : (
            <Card className="divide-y-(--hairline) divide-border">
              {data.posts.map((post) => (
                <PostRow
                  key={post.id}
                  post={post}
                  zone={zone}
                  canManage={canManage}
                  error={rowError?.id === post.id ? rowError.text : null}
                  onEdit={() => setComposing({ post, input: inputFrom(post, new Date(), zone) })}
                  onDuplicate={() => setComposing({ post: null, input: duplicateOf(post, new Date(), zone) })}
                  onPostNow={() => act(post, () => postsApi.sendNow(post.id))}
                  onCancel={() => setConfirming({ kind: 'cancel', post })}
                  onDeleteDraft={() => setConfirming({ kind: 'delete-draft', post })}
                  onTryAgain={(d) => act(post, () => postsApi.tryAgain(post.id, d.id))}
                  onEditOnSite={(d) => setEditing({ post, destination: d })}
                  onDeleteOnSite={(d) => setConfirming({ kind: 'delete-on-site', post, destination: d })}
                />
              ))}
            </Card>
          )}

          {pages > 1 && (
            <div className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
              <Button size="xs" variant="outline" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                Newer
              </Button>
              <span className="font-mono text-muted-foreground">
                {page} / {pages}
              </span>
              <Button size="xs" variant="outline" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
                Older
              </Button>
            </div>
          )}
        </div>
      </Tabs>

      {composing && (
        <PostComposer
          post={composing.post}
          initial={composing.input}
          sites={data.sites}
          roles={data.vrChatRoles}
          onClose={() => setComposing(null)}
          onSaved={(saved) => {
            setComposing(null)
            setCancelled(false)
            const next = listAfterSave(saved)
            if (next !== tab) onList(PAGE_OF[next])
            else void load()
          }}
        />
      )}

      {editing && (
        <SiteEdit
          post={editing.post}
          destination={editing.destination}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            void load()
          }}
        />
      )}

      <ConfirmDialog
        open={confirming?.kind === 'cancel'}
        onOpenChange={(open) => !open && setConfirming(null)}
        title="Cancel this post?"
        action="Cancel post"
        failed="Could not cancel the post."
        onConfirm={() => (confirming ? postsApi.cancel(confirming.post.id) : Promise.resolve())}
        onDone={() => {
          setConfirming(null)
          void load()
        }}
      />
      <ConfirmDialog
        open={confirming?.kind === 'delete-draft'}
        onOpenChange={(open) => !open && setConfirming(null)}
        title="Delete this draft?"
        action="Delete draft"
        failed="Could not delete the draft."
        onConfirm={() => (confirming ? postsApi.deleteDraft(confirming.post.id) : Promise.resolve())}
        onDone={() => {
          setConfirming(null)
          void load()
        }}
      />
      <ConfirmDialog
        open={confirming?.kind === 'delete-on-site'}
        onOpenChange={(open) => !open && setConfirming(null)}
        title={
          confirming?.kind === 'delete-on-site'
            ? `Delete this post on ${NETWORK_LABEL[confirming.destination.network]}?`
            : 'Delete this post?'
        }
        action="Delete"
        failed="Could not delete the post."
        onConfirm={() =>
          confirming?.kind === 'delete-on-site'
            ? postsApi.deleteOnSite(confirming.post.id, confirming.destination.id)
            : Promise.resolve()
        }
        onDone={() => {
          setConfirming(null)
          void load()
        }}
      />
    </div>
  )
}

/** "Fri, Oct 3, 8:00 PM", in the viewer's time, with the post's zone after it when that is another. */
function whenText(post: Post, zone: string): string {
  if (!post.sendAt) return 'No time'
  const at = dateTimeWithWeekday(post.sendAt)
  return post.timeZone !== zone ? `${at} · ${post.timeZone}` : at
}

const TONE_BADGE = { good: 'ok', warn: 'warn', bad: 'destructive', quiet: 'secondary' } as const

function PostRow({
  post,
  zone,
  canManage,
  error,
  onEdit,
  onDuplicate,
  onPostNow,
  onCancel,
  onDeleteDraft,
  onTryAgain,
  onEditOnSite,
  onDeleteOnSite,
}: {
  post: Post
  zone: string
  canManage: boolean
  error: string | null
  onEdit: () => void
  onDuplicate: () => void
  onPostNow: () => void
  onCancel: () => void
  onDeleteDraft: () => void
  onTryAgain: (destination: PostDestination) => void
  onEditOnSite: (destination: PostDestination) => void
  onDeleteOnSite: (destination: PostDestination) => void
}) {
  return (
    <div className="flex flex-col gap-1.5 px-(--panel-pad) py-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="shrink-0 font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {whenText(post, zone)}
        </span>
        <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{headline(post) || 'Untitled'}</span>
        {post.eventTitle && (
          <a href="/calendar" onClick={followLink('/calendar')} className="text-link hover:underline" style={{ fontSize: 'var(--text-small)' }}>
            {post.eventTitle}
          </a>
        )}
        {post.createdBy && (
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {post.createdBy}
          </span>
        )}
        <div className="flex-1" />
        {canManage && (
          <span className="flex flex-wrap gap-1.5">
            {canEditWhole(post) && (
              <Button size="xs" variant="outline" onClick={onEdit}>
                Edit
              </Button>
            )}
            {canPostNow(post) && (
              <Button size="xs" variant="outline" onClick={onPostNow}>
                Post now
              </Button>
            )}
            {canCancel(post) && (
              <Button size="xs" variant="outline" onClick={onCancel}>
                Cancel post
              </Button>
            )}
            {post.status === 'draft' && (
              <Button size="xs" variant="outline" onClick={onDeleteDraft}>
                Delete draft
              </Button>
            )}
            <Button size="xs" variant="ghost" onClick={onDuplicate}>
              Duplicate
            </Button>
          </span>
        )}
      </div>

      {post.destinations.map((d) => (
        <div key={d.id} className="flex flex-wrap items-center gap-x-2 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
          <Badge variant={TONE_BADGE[shownTone(d.shown)]}>
            {NETWORK_LABEL[d.network]} · {shownLabel(d)}
          </Badge>
          {d.targetName && <span className="text-muted-foreground">#{d.targetName}</span>}
          {d.vrChat && <span className="text-muted-foreground">{vrchatAudience(d.vrChat.visibility, d.vrChat.roleNames)}</span>}
          {(d.state === 'failed' || d.notPublished) && d.error && <span className="text-destructive">{d.error}</span>}
          {d.missingPermission && (
            <span className="text-destructive">Needs {vrchatPermissionLabel(d.missingPermission)}</span>
          )}
          {d.link && d.state === 'posted' && (
            <a href={d.link} target="_blank" rel="noreferrer" className="text-link hover:underline">
              Open
            </a>
          )}
          {canManage && (d.state === 'failed' || d.notPublished) && post.status === 'scheduled' && (
            <Button size="xs" variant="outline" onClick={() => onTryAgain(d)}>
              Try again
            </Button>
          )}
          {canManage && d.state === 'posted' && (d.network === 'discord' || d.network === 'vrchat') && (
            <Button size="xs" variant="ghost" onClick={() => onEditOnSite(d)}>
              Edit
            </Button>
          )}
          {canManage && d.state === 'posted' && (
            <Button size="xs" variant="ghost" onClick={() => onDeleteOnSite(d)}>
              Delete
            </Button>
          )}
        </div>
      ))}

      {error && <Outcome tone="problem">{error}</Outcome>}
    </div>
  )
}

/**
 * Edit on a site after the post went out (posts design §4.5): the title and the text only, for that
 * site alone. Discord keeps the picture and pings nobody; VRChat keeps the picture and who sees it,
 * notifies nobody, and needs a title.
 */
function SiteEdit({
  post,
  destination,
  onClose,
  onSaved,
}: {
  post: Post
  destination: PostDestination
  onClose: () => void
  onSaved: () => void
}) {
  const [title, setTitle] = useState(destination.titleOverride ?? post.title ?? '')
  const [text, setText] = useState(destination.textOverride ?? post.text)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const vrchat = destination.network === 'vrchat'
  const length = vrchat ? tidy(text).length : discordMessage(title, text, destination.roleId).length
  const needsTitle = vrchat && !tidy(title)

  const save = () => {
    setBusy(true)
    setError(null)

    postsApi
      .editOnSite(post.id, destination.id, title.trim() || null, text)
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not edit the post.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={`Edit on ${NETWORK_LABEL[destination.network]}`}
        className="max-w-[640px]"
        foot={
          <DialogFoot>
            {error && (
              <span role="alert" className="mr-auto">
                <Outcome tone="problem">{error}</Outcome>
              </span>
            )}
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            <Button size="sm" disabled={busy || needsTitle} onClick={save}>
              Save
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
          <label className="flex flex-col gap-1">
            <span className="text-muted-foreground">Title</span>
            <Input value={title} onChange={(e) => setTitle(e.target.value)} />
            {needsTitle && <span className="text-destructive">Needs a title</span>}
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-muted-foreground">Text</span>
            <Textarea rows={6} value={text} onChange={(e) => setText(e.target.value)} />
          </label>
          {vrchat ? (
            <span className="font-mono text-muted-foreground">VRChat {length}</span>
          ) : (
            <span className={cn('font-mono', length > DISCORD_LIMIT ? 'text-destructive' : 'text-muted-foreground')}>
              Discord {length} / {DISCORD_LIMIT}
            </span>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
