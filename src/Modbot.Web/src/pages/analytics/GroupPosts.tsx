import { useCallback, useEffect, useState } from 'react'
import { Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { dateTime } from '@/components/charts'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { PersonLink } from '@/components/facts'
import { FieldRow, LongBox, SaveCancel, TextBox } from '@/components/group/ProfileEditors'
import { Pager } from '@/components/Pager'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { useSave } from '@/lib/useSave'
import { Checkbox } from '@/components/ui/checkbox'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Select } from '@/components/ui/select'
import { api, ApiError, type CurrentUser, type GroupPostList, type GroupPostRow, type GroupRoleChoice } from '@/lib/api'
import { VISIBILITIES, audience, draftOf, emptyDraft, postBody, postPages, postProblem, type PostDraft } from '@/lib/groupPosts'
import { useListPage } from '@/lib/listPage'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { useShortcuts } from '@/lib/shortcuts'
import { GroupHeaderFor } from './GroupHeader'

/** The list each page of posts last showed, by page number, kept while the app is open. */
const lastLists = new Map<number, GroupPostList>()

/**
 * The VRChat page's Posts tab: the group's posts as vrchat.com lists them, newest first, and — for
 * whoever may manage them — a new post, an edit and a delete.
 *
 * **Every request to VRChat here is one somebody asked for.** The list is read once when the tab
 * opens, once per page turned and once per Refresh; nothing polls it. A Post, a Save and a Delete
 * are one request each. A refusal is shown in VRChat's own words and nothing is tried again.
 */
export function GroupPosts({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const at = useListPage()
  const { page } = at
  // Bumped by Refresh, so the same page can be read again on purpose.
  const [asked, setAsked] = useState(0)
  // The last answer, with the read it answered: a read whose answer has not come back yet is the
  // one being waited for, and the last list stays on screen meanwhile.
  const wanted = `${page}:${asked}`
  // Coming back to the tab draws the list it last showed while the tab's one read is in flight,
  // rather than "Loading…" in its place.
  const [read, setRead] = useState<{ key: string; list: GroupPostList | null; error: string | null } | null>(() => {
    const shown = lastLists.get(page)
    return shown ? { key: 'shown before', list: shown, error: null } : null
  })
  const loading = read?.key !== wanted
  const list = read?.list ?? null
  const error = read?.key === wanted ? read.error : null
  const setList = (change: (list: GroupPostList) => GroupPostList) =>
    setRead((current) => (current?.list ? { ...current, list: change(current.list) } : current))

  const manages = can(me, 'ManageGroupPosts')
  const [editing, setEditing] = useState<GroupPostRow | 'new' | null>(null)
  const [deleting, setDeleting] = useState<GroupPostRow | null>(null)

  useEffect(() => {
    let cancelled = false
    const key = `${page}:${asked}`

    api
      .groupPosts(page)
      .then((next) => {
        if (!cancelled) setRead({ key, list: next, error: null })
      })
      .catch((e: unknown) => {
        if (!cancelled)
          setRead((current) => ({
            key,
            list: current?.list ?? null,
            error: e instanceof ApiError ? e.message : 'Could not read the posts.',
          }))
      })

    return () => {
      cancelled = true
    }
  }, [page, asked])

  // Whatever the list now shows -- a read, a new post, an edit, a delete -- is what the tab shows
  // next time it opens.
  useEffect(() => {
    if (read?.list) lastLists.set(page, read.list)
  }, [read, page])

  const refresh = useCallback(() => setAsked((n) => n + 1), [])

  // A new post goes to the top of page one; an edited one is replaced where it is. Neither costs a
  // second read: the list shows what VRChat answered the write with.
  const saved = (post: GroupPostRow, created: boolean) => {
    setEditing(null)
    if (created && page !== 1) {
      at.goTo(1)
      return
    }
    setList((current) => ({
      ...current,
      total: created ? current.total + 1 : current.total,
      posts: created ? [post, ...current.posts] : current.posts.map((p) => (p.id === post.id ? post : p)),
    }))
  }

  const removed = (post: GroupPostRow) =>
    setList((current) => ({
      ...current,
      total: Math.max(0, current.total - 1),
      posts: current.posts.filter((p) => p.id !== post.id),
    }))

  const roles = list?.roles ?? []

  useShortcuts([
    { label: 'Refresh posts', group: 'Page', page: true, run: refresh },
    ...(manages ? [{ label: 'New post', group: 'Page' as const, page: true, run: () => setEditing('new') }] : []),
  ])

  return (
    <div className="flex flex-col gap-3">
      <GroupHeaderFor me={me} pathOf={pathOf} active="group-posts" />

      <PanelGrid className="grid-cols-1">
        <Card>
          <CardHeader>
            <CardTitle>Posts</CardTitle>
            <CardAction>
              <Button size="xs" variant="outline" onClick={refresh} disabled={loading} aria-label="Refresh posts">
                <RefreshCw className={loading ? 'animate-spin' : undefined} /> Refresh
              </Button>
              {manages && (
                <Button size="xs" onClick={() => setEditing('new')}>
                  <Plus /> New post
                </Button>
              )}
            </CardAction>
          </CardHeader>

          {error ? (
            <EmptyRow tone="danger" onTryAgain={refresh}>{error}</EmptyRow>
          ) : !list ? (
            <EmptyRow tone="loading" />
          ) : list.posts.length === 0 ? (
            <EmptyRow>No posts</EmptyRow>
          ) : (
            <ul className="divide-y-(--hairline) divide-border">
              {list.posts.map((post) => (
                <li key={post.id}>
                  <PostView
                    post={post}
                    roles={roles}
                    onEdit={manages ? () => setEditing(post) : undefined}
                    onDelete={manages ? () => setDeleting(post) : undefined}
                  />
                </li>
              ))}
            </ul>
          )}

          {list && postPages(list.total, list.pageSize) > 1 && <Pager at={at} pages={postPages(list.total, list.pageSize)} />}
        </Card>
      </PanelGrid>

      <PostEditor
        open={editing !== null}
        post={editing === 'new' ? null : editing}
        roles={roles}
        onOpenChange={(open) => !open && setEditing(null)}
        onSaved={saved}
      />

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={deleting?.title ? `Delete “${deleting.title}”?` : 'Delete this post?'}
        action="Delete"
        failed="Could not delete the post."
        onConfirm={() => api.deleteGroupPost(deleting!.id, deleting!.title)}
        onDone={() => deleting && removed(deleting)}
      />
    </div>
  )
}

/** One post, the way vrchat.com shows it: title, who and when, picture, text, and who sees it. */
function PostView({
  post,
  roles,
  onEdit,
  onDelete,
}: {
  post: GroupPostRow
  roles: readonly GroupRoleChoice[]
  onEdit?: () => void
  onDelete?: () => void
}) {
  const picture = vrchatMedia(post.imageUrl)
  const edited = post.updatedAt && post.createdAt && post.updatedAt !== post.createdAt

  return (
    <article className="flex flex-col gap-2 p-(--panel-pad)">
      <div className="flex items-start gap-2">
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <h3 className="font-medium break-words" style={{ fontSize: 'calc(var(--text-base) * 1.15)' }}>
            {post.title ?? <span className="text-muted-foreground">Untitled</span>}
          </h3>
          <div
            className="flex flex-wrap items-center gap-x-2 gap-y-1 text-muted-foreground"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {post.authorId && <PersonLink platform="vrchat" id={post.authorId} name={post.authorName} />}
            {post.createdAt && <span className="font-mono">{dateTime(post.createdAt)}</span>}
            {edited && <span>edited</span>}
            <Badge variant={post.visibility === 'public' ? 'outline' : 'secondary'}>{audience(post, roles)}</Badge>
          </div>
        </div>

        {(onEdit || onDelete) && (
          <div className="flex shrink-0 items-center gap-1">
            {onEdit && (
              <Button variant="ghost" size="icon-sm" aria-label="Edit post" title="Edit" onClick={onEdit}>
                <Pencil />
              </Button>
            )}
            {onDelete && (
              <Button variant="ghost" size="icon-sm" aria-label="Delete post" title="Delete" onClick={onDelete}>
                <Trash2 />
              </Button>
            )}
          </div>
        )}
      </div>

      {picture && (
        <img
          src={picture}
          alt=""
          loading="lazy"
          referrerPolicy="no-referrer"
          className="max-h-80 w-full max-w-xl border border-(length:--hairline) bg-muted object-contain"
        />
      )}

      {post.text && <p className="break-words whitespace-pre-wrap">{post.text}</p>}
    </article>
  )
}

/** The new-post and edit form, in a dialog so the list stays where it was underneath. */
function PostEditor({
  open,
  post,
  roles,
  onOpenChange,
  onSaved,
}: {
  open: boolean
  /** The post being changed, or null for a new one. */
  post: GroupPostRow | null
  roles: readonly GroupRoleChoice[]
  onOpenChange: (open: boolean) => void
  onSaved: (post: GroupPostRow, created: boolean) => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Mounted only while open, so each opening starts from the post, not from the last draft. */}
      {open && (
        <DialogContent title={post ? 'Edit post' : 'New post'} className="max-w-[640px]">
          <PostForm post={post} roles={roles} onCancel={() => onOpenChange(false)} onSaved={onSaved} />
        </DialogContent>
      )}
    </Dialog>
  )
}

function PostForm({
  post,
  roles,
  onCancel,
  onSaved,
}: {
  post: GroupPostRow | null
  roles: readonly GroupRoleChoice[]
  onCancel: () => void
  onSaved: (post: GroupPostRow, created: boolean) => void
}) {
  const [draft, setDraft] = useState<PostDraft>(() => (post ? draftOf(post) : emptyDraft()))
  const { saving, problem, missing, run } = useSave(post ? 'Could not save the post.' : 'Could not post.')
  const invalid = postProblem(draft)
  const set = (change: Partial<PostDraft>) => setDraft((d) => ({ ...d, ...change }))

  const send = () => {
    const body = postBody(draft, post)

    void run(async () => {
      const result = post ? await api.updateGroupPost(body) : await api.createGroupPost(body)
      onSaved(result.post, post === null)
    })
  }

  const toggleRole = (id: string, on: boolean) =>
    set({ roleIds: on ? [...draft.roleIds, id] : draft.roleIds.filter((r) => r !== id) })

  return (
    <div className="flex flex-col gap-3">
      <FieldRow label="Title">{(id) => <TextBox id={id} value={draft.title} onChange={(title) => set({ title })} />}</FieldRow>

      <FieldRow label="Text">{(id) => <LongBox id={id} rows={6} value={draft.text} onChange={(text) => set({ text })} />}</FieldRow>

      <FieldRow label="Who can see it">
        {() => (
          <Select
            aria-label="Who can see it"
            value={draft.visibility}
            className="w-full sm:w-64"
            onChange={(v) => set({ visibility: v === 'public' ? 'public' : 'group' })}
          >
            {VISIBILITIES.map((v) => (
              <option key={v.value} value={v.value}>
                {v.label}
              </option>
            ))}
          </Select>
        )}
      </FieldRow>

      {draft.visibility === 'group' && roles.length > 0 && (
        <fieldset className="flex flex-col gap-1.5">
          <legend className="mb-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Only these roles
          </legend>
          <div className="grid grid-cols-1 gap-x-4 gap-y-1.5 sm:grid-cols-2">
            {roles.map((role) => (
              <Checkbox
                key={role.id}
                checked={draft.roleIds.includes(role.id)}
                onChange={(on) => toggleRole(role.id, on)}
              >
                {role.name}
              </Checkbox>
            ))}
          </div>
        </fieldset>
      )}

      {!post && (
        <Checkbox checked={draft.notify} onChange={(notify) => set({ notify })}>
          Notify members
        </Checkbox>
      )}

      {post?.imageUrl && vrchatMedia(post.imageUrl) && (
        <img
          src={vrchatMedia(post.imageUrl)!}
          alt=""
          referrerPolicy="no-referrer"
          className="max-h-40 self-start border border-(length:--hairline) bg-muted object-contain"
        />
      )}

      <SaveCancel
        saving={saving}
        missing={missing}
        disabled={invalid !== null}
        problem={problem}
        onCancel={onCancel}
        onSave={send}
        save={post ? 'Save' : 'Post'}
      />
    </div>
  )
}
