// Relative, with the extension, so the Node test runner can load this file as it is (see nav.ts).
import type { GroupPostBody, GroupPostRow, GroupRoleChoice, PostVisibility } from './api.ts'

/**
 * The pieces of the VRChat page's Posts tab worth a test: who a post is for, in words; the checks
 * before a post is sent; the body an edit sends; and how many pages the list has.
 */

export const VISIBILITIES: readonly { value: PostVisibility; label: string }[] = [
  { value: 'group', label: 'Members only' },
  { value: 'public', label: 'Everyone' },
]

/**
 * Who sees a post, as the page says it: "Everyone", "Members only", or the roles it is limited
 * to by name. A role Modbot has no name for is shown by its id rather than dropped.
 */
export function audience(post: Pick<GroupPostRow, 'visibility' | 'roleIds'>, roles: readonly GroupRoleChoice[]): string {
  if (post.visibility === 'public') return 'Everyone'
  if (post.roleIds.length === 0) return 'Members only'

  const names = post.roleIds.map((id) => roles.find((r) => r.id === id)?.name ?? id)
  return names.join(', ')
}

/** A post as the form holds it. */
export type PostDraft = {
  title: string
  text: string
  visibility: PostVisibility
  roleIds: string[]
  notify: boolean
}

export function emptyDraft(): PostDraft {
  return { title: '', text: '', visibility: 'group', roleIds: [], notify: false }
}

export function draftOf(post: GroupPostRow): PostDraft {
  return {
    title: post.title ?? '',
    text: post.text ?? '',
    visibility: post.visibility,
    roleIds: [...post.roleIds],
    notify: false,
  }
}

/** The first thing wrong with a post, or null. VRChat needs a title and some text. */
export function postProblem(draft: PostDraft): string | null {
  if (!draft.title.trim()) return 'A post needs a title.'
  if (!draft.text.trim()) return 'A post needs some text.'
  return null
}

/**
 * The body a Post or a Save sends. An edit carries the post's id and its picture's id, because
 * VRChat replaces the whole post and a picture not sent again is removed. Roles only apply to a
 * post for members, so a post for everyone sends none.
 */
export function postBody(draft: PostDraft, editing: GroupPostRow | null): GroupPostBody {
  return {
    id: editing?.id ?? null,
    title: draft.title.trim(),
    text: draft.text.trim(),
    visibility: draft.visibility,
    roleIds: draft.visibility === 'group' ? [...draft.roleIds] : [],
    notify: editing ? false : draft.notify,
    imageId: editing?.imageId ?? null,
  }
}

/** How many pages a list of `total` posts takes, never fewer than one. */
export function postPages(total: number, pageSize: number): number {
  if (pageSize <= 0) return 1
  return Math.max(1, Math.ceil(total / pageSize))
}
