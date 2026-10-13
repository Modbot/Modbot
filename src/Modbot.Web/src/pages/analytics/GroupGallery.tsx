import { useEffect, useState } from 'react'
import { Check, RefreshCw, Trash2, X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { PersonLink } from '@/components/facts'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Select } from '@/components/ui/select'
import {
  api,
  ApiError,
  type CurrentUser,
  type GallerySubmissionRow,
  type GroupGalleryChoice,
  type GroupGalleryImageRow,
  type GroupGalleryPage,
  type MissingGroupPermission,
} from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { missingPermissionOf } from '@/lib/vrchatPermissions'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { useShortcuts } from '@/lib/shortcuts'
import { GroupHeaderFor } from './GroupHeader'

/**
 * The VRChat page's Gallery tab: the group's galleries, and the chosen one's images as a grid, as
 * vrchat.com shows them. Images waiting for approval are marked, and can be listed alone. Whoever
 * may manage the gallery can remove an image, after being asked. Under the grid, the pictures
 * members posted in Discord wait for that person to add them to a gallery or decline them.
 *
 * **Every request to VRChat here is one somebody asked for.** The galleries come with the group
 * poll Modbot already makes; one request reads a gallery's images when the tab opens, when another
 * gallery is chosen, per page and per Refresh. A removal is one request; adding a picture from
 * Discord is two (the upload, then the gallery). The waiting pictures are Modbot's own and cost
 * VRChat nothing.
 */
export function GroupGallery({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [galleryId, setGalleryId] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const [asked, setAsked] = useState(0)
  const [waiting, setWaiting] = useState(false)
  const wanted = `${galleryId ?? ''}:${page}:${asked}:${waiting}`
  const [read, setRead] = useState<{
    key: string
    view: GroupGalleryPage | null
    error: string | null
    missing: MissingGroupPermission | null
  } | null>(null)
  const loading = read?.key !== wanted
  const view = read?.view ?? null
  const error = read?.key === wanted ? read.error : null
  const missing = read?.key === wanted ? read.missing : null

  const manages = can(me, 'ManageGroupGallery')

  useShortcuts([{ label: 'Refresh gallery', group: 'Page', page: true, run: () => setAsked((n) => n + 1) }])
  const [removing, setRemoving] = useState<GroupGalleryImageRow | null>(null)

  useEffect(() => {
    let cancelled = false
    const key = `${galleryId ?? ''}:${page}:${asked}:${waiting}`

    api
      .groupGallery(galleryId, page, waiting)
      .then((next) => {
        if (!cancelled) setRead({ key, view: next, error: null, missing: null })
      })
      .catch((e: unknown) => {
        if (!cancelled)
          setRead((current) => ({
            key,
            view: current?.view ?? null,
            error: e instanceof ApiError ? e.message : 'Could not read the gallery.',
            missing: e instanceof ApiError ? missingPermissionOf(e.detail) : null,
          }))
      })

    return () => {
      cancelled = true
    }
  }, [galleryId, page, asked, waiting])

  const choose = (id: string) => {
    setGalleryId(id)
    setPage(1)
  }

  const show = (value: string) => {
    setWaiting(value === 'waiting')
    setPage(1)
  }

  const removed = (image: GroupGalleryImageRow) =>
    setRead((current) =>
      current?.view ? { ...current, view: { ...current.view, images: current.view.images.filter((i) => i.id !== image.id) } } : current,
    )

  const galleries = view?.galleries ?? []
  const shown = view?.galleryId ?? galleryId

  return (
    <div className="flex flex-col gap-3">
      <GroupHeaderFor me={me} pathOf={pathOf} active="group-gallery" />

      <PanelGrid className="grid-cols-1">
        <Card>
          <CardHeader>
            <CardTitle>Gallery</CardTitle>
            <CardAction>
              <Select aria-label="Show" value={waiting ? 'waiting' : 'all'} className="w-48" onChange={show}>
                <option value="all">All images</option>
                <option value="waiting">Waiting for approval</option>
              </Select>
              {galleries.length > 1 && (
                <Select aria-label="Gallery" value={shown ?? ''} className="w-48" onChange={choose}>
                  {galleries.map((g) => (
                    <option key={g.id} value={g.id}>
                      {g.name ?? g.id}
                    </option>
                  ))}
                </Select>
              )}
              <Button size="xs" variant="outline" onClick={() => setAsked((n) => n + 1)} disabled={loading} aria-label="Refresh gallery">
                <RefreshCw className={loading ? 'animate-spin' : undefined} /> Refresh
              </Button>
            </CardAction>
          </CardHeader>

          {missing ? (
            <EmptyRow tone="danger" onTryAgain={() => setAsked((n) => n + 1)}>
              <VRChatPermissionMissing missing={missing} />
            </EmptyRow>
          ) : error ? (
            <EmptyRow tone="danger" onTryAgain={() => setAsked((n) => n + 1)}>{error}</EmptyRow>
          ) : !view ? (
            <EmptyRow tone="loading" />
          ) : galleries.length === 0 && !view.galleryId ? (
            <EmptyRow>No galleries</EmptyRow>
          ) : view.images.length === 0 ? (
            <EmptyRow>{waiting ? 'Nothing waiting for approval' : 'No images'}</EmptyRow>
          ) : (
            <ul className="grid grid-cols-2 gap-2 p-(--panel-pad) sm:grid-cols-3 lg:grid-cols-4">
              {view.images.map((image) => (
                <li key={image.id}>
                  <ImageTile image={image} onRemove={manages ? () => setRemoving(image) : undefined} />
                </li>
              ))}
            </ul>
          )}

          {view && (view.page > 1 || view.hasMore) && (
            <CardFooter className="flex-wrap gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <Button size="xs" variant="outline" disabled={view.page <= 1 || loading} onClick={() => setPage((p) => p - 1)}>
                Previous
              </Button>
              <Button size="xs" variant="outline" disabled={!view.hasMore || loading} onClick={() => setPage((p) => p + 1)}>
                Next
              </Button>
            </CardFooter>
          )}
        </Card>

        <Submissions
          galleries={galleries}
          shown={shown}
          manages={manages}
          asked={asked}
          onAdded={() => setAsked((n) => n + 1)}
        />
      </PanelGrid>

      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => !open && setRemoving(null)}
        title="Remove this image from the gallery?"
        action="Remove"
        failed="Could not remove the image."
        onConfirm={() => api.removeGroupGalleryImage(shown!, removing!.id, removing!.submittedById)}
        onDone={() => removing && removed(removing)}
      />
    </div>
  )
}

/**
 * The pictures members posted in the Discord gallery channel, oldest first, each with the gallery
 * it will go to, Add and Decline. Read from Modbot's own database, so reading it costs VRChat
 * nothing; Add is the two requests to VRChat, and a failure stays under the picture it belongs to.
 */
function Submissions({
  galleries,
  shown,
  manages,
  asked,
  onAdded,
}: {
  galleries: GroupGalleryChoice[]
  shown: string | null
  manages: boolean
  asked: number
  onAdded: () => void
}) {
  const [list, setList] = useState<GallerySubmissionRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [again, setAgain] = useState(0)
  const [to, setTo] = useState<string | null>(null)
  const [adding, setAdding] = useState<string | null>(null)
  const [problems, setProblems] = useState<Record<string, { message: string; missing: MissingGroupPermission | null }>>({})
  const [declining, setDeclining] = useState<GallerySubmissionRow | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .groupGallerySubmissions()
      .then((next) => {
        if (cancelled) return
        setList(next.submissions)
        setError(null)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not read the pictures from Discord.')
      })

    return () => {
      cancelled = true
    }
  }, [asked, again])

  const target = to ?? shown ?? galleries[0]?.id ?? null
  const gone = (id: string) => setList((current) => current?.filter((s) => s.id !== id) ?? current)

  const add = (submission: GallerySubmissionRow) => {
    if (!target) return

    setAdding(submission.id)
    setProblems((current) => Object.fromEntries(Object.entries(current).filter(([id]) => id !== submission.id)))

    api
      .addGroupGallerySubmission(submission.id, target)
      .then(() => {
        gone(submission.id)
        onAdded()
      })
      .catch((e: unknown) =>
        setProblems((current) => ({
          ...current,
          [submission.id]: {
            message: e instanceof ApiError ? e.message : 'Could not add the picture.',
            missing: e instanceof ApiError ? missingPermissionOf(e.detail) : null,
          },
        })),
      )
      .finally(() => setAdding(null))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Pictures from Discord</CardTitle>
        {manages && galleries.length > 1 && (
          <CardAction>
            <Select aria-label="Add to" value={target ?? ''} className="w-48" onChange={setTo}>
              {galleries.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name ?? g.id}
                </option>
              ))}
            </Select>
          </CardAction>
        )}
      </CardHeader>

      {error ? (
        <EmptyRow tone="danger" onTryAgain={() => setAgain((n) => n + 1)}>{error}</EmptyRow>
      ) : !list ? (
        <EmptyRow tone="loading" />
      ) : list.length === 0 ? (
        <EmptyRow>No pictures waiting</EmptyRow>
      ) : (
        <ul className="grid grid-cols-2 gap-2 p-(--panel-pad) sm:grid-cols-3 lg:grid-cols-4">
          {list.map((submission) => {
            const problem = problems[submission.id]

            return (
              <li key={submission.id}>
                <figure className="flex flex-col gap-1">
                  <img
                    src={submission.pictureUrl}
                    alt=""
                    loading="lazy"
                    className="aspect-video w-full border border-(length:--hairline) bg-muted object-cover"
                  />
                  <figcaption className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                    <span className="truncate">
                      <PersonLink platform="discord" id={submission.authorId} name={submission.authorName} />
                    </span>
                    <a
                      href={submission.messageUrl}
                      target="_blank"
                      rel="noreferrer"
                      className="truncate text-muted-foreground hover:underline"
                    >
                      Open in Discord
                    </a>
                    {manages && (
                      <span className="flex gap-1">
                        <Button
                          size="xs"
                          onClick={() => add(submission)}
                          disabled={!target || adding !== null}
                          aria-label="Add picture to the gallery"
                        >
                          <Check /> {adding === submission.id ? 'Adding…' : 'Add'}
                        </Button>
                        <Button
                          size="xs"
                          variant="outline"
                          onClick={() => setDeclining(submission)}
                          disabled={adding === submission.id}
                          aria-label="Decline picture"
                        >
                          <X /> Decline
                        </Button>
                      </span>
                    )}
                    {problem &&
                      (problem.missing ? (
                        <VRChatPermissionMissing missing={problem.missing} className="text-destructive" />
                      ) : (
                        <span className="text-destructive">{problem.message}</span>
                      ))}
                  </figcaption>
                </figure>
              </li>
            )
          })}
        </ul>
      )}

      <ConfirmDialog
        open={declining !== null}
        onOpenChange={(open) => !open && setDeclining(null)}
        title="Decline this picture?"
        action="Decline"
        failed="Could not decline the picture."
        onConfirm={() => api.declineGroupGallerySubmission(declining!.id)}
        onDone={() => declining && gone(declining.id)}
      />
    </Card>
  )
}

function ImageTile({ image, onRemove }: { image: GroupGalleryImageRow; onRemove?: () => void }) {
  const picture = vrchatMedia(image.imageUrl)

  return (
    <figure className="flex flex-col gap-1">
      <div className="relative">
        {picture ? (
          <img
            src={picture}
            alt=""
            loading="lazy"
            referrerPolicy="no-referrer"
            className="aspect-video w-full border border-(length:--hairline) bg-muted object-cover"
          />
        ) : (
          <div className="aspect-video w-full border border-(length:--hairline) bg-muted" />
        )}
        {!image.approved && (
          <Badge variant="secondary" className="absolute top-1 left-1">
            Pending
          </Badge>
        )}
        {onRemove && (
          <Button
            variant="secondary"
            size="icon-xs"
            aria-label="Remove image"
            title="Remove"
            onClick={onRemove}
            className="absolute top-1 right-1"
          >
            <Trash2 />
          </Button>
        )}
      </div>
      {image.submittedById && (
        <figcaption className="truncate" style={{ fontSize: 'var(--text-small)' }}>
          <PersonLink platform="vrchat" id={image.submittedById} name={image.submittedByName} />
        </figcaption>
      )}
    </figure>
  )
}
