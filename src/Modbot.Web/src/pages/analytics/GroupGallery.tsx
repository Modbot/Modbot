import { useEffect, useState } from 'react'
import { RefreshCw, Trash2 } from 'lucide-react'
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
 * vrchat.com shows them. Images waiting for approval are marked. Whoever may manage the gallery can
 * remove an image, after being asked.
 *
 * **Every request to VRChat here is one somebody asked for.** The galleries come with the group
 * poll Modbot already makes; one request reads a gallery's images when the tab opens, when another
 * gallery is chosen, per page and per Refresh. A removal is one request. Adding an image needs
 * VRChat's file upload, which Modbot does not do yet.
 */
export function GroupGallery({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [galleryId, setGalleryId] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const [asked, setAsked] = useState(0)
  const wanted = `${galleryId ?? ''}:${page}:${asked}`
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
    const key = `${galleryId ?? ''}:${page}:${asked}`

    api
      .groupGallery(galleryId, page)
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
  }, [galleryId, page, asked])

  const choose = (id: string) => {
    setGalleryId(id)
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
            <EmptyRow tone="danger">
              <VRChatPermissionMissing missing={missing} />
            </EmptyRow>
          ) : error ? (
            <EmptyRow tone="danger">{error}</EmptyRow>
          ) : !view ? (
            <EmptyRow>Loading…</EmptyRow>
          ) : galleries.length === 0 && !view.galleryId ? (
            <EmptyRow>No galleries</EmptyRow>
          ) : view.images.length === 0 ? (
            <EmptyRow>No images</EmptyRow>
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
