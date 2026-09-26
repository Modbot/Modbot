import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { CardFooter } from '@/components/ui/card'
import { api, ApiError, type EvidenceDelivery, type EvidenceItem } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { bytes } from '@/components/settings/units'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { cn } from '@/lib/utils'

/**
 * The evidence attached to a case file, and the control that attaches more.
 *
 * Images are fetched with the session cookie and shown from a `blob:` URL typed from Modbot's own
 * determination of the content type, never from the response (evidence design §10.4) -- so the
 * bytes are never reachable at a navigable same-origin URL and the object URL can never be typed
 * as HTML. Video is played straight from the serving endpoint, because seeking is HTTP range
 * requests and a blob of a hundred-megabyte clip would have to arrive whole first; on a store
 * that can hand out links the endpoint redirects and the bucket does the streaming.
 *
 * Uploading is the three phases of §9.1 driven the way a browser drives them: begin, the bytes as
 * a raw body with a real progress indicator, commit. The bytes go where the ticket says -- to
 * Modbot, or straight to the bucket -- and the store's delivery behaviour is stated in the same
 * words the Settings evidence card uses, because it is the same fact.
 */
export function EvidenceGallery({
  caseId,
  items,
  delivery,
  canAttach,
  onChanged,
  onImageReady,
}: {
  caseId: string
  items: EvidenceItem[]
  delivery: EvidenceDelivery
  canAttach: boolean
  onChanged: () => void
  /** Tells the page an image's object URL, so the written reason can show `evidence:` references inline. */
  onImageReady?: (hash: string, url: string) => void
}) {
  return (
    <div className="flex flex-col">
      {items.length === 0 ? (
        <EmptyRow>Nothing attached.</EmptyRow>
      ) : (
        // Edge to edge in its panel, so each item's lines are the panel's own. Two across only when
        // there are two to put side by side; a lone item takes the width.
        <PanelGrid as="ul" className={cn('m-0', items.length > 1 && 'sm:grid-cols-2')}>
          {items.map((item) => (
            <li key={item.hash} className="p-(--panel-pad)">
              <Item item={item} onImageReady={onImageReady} />
            </li>
          ))}
        </PanelGrid>
      )}

      {canAttach && <Attach caseId={caseId} delivery={delivery} onChanged={onChanged} />}
    </div>
  )
}

function Item({ item, onImageReady }: { item: EvidenceItem; onImageReady?: (hash: string, url: string) => void }) {
  const name = item.fileName ?? `${item.hash.slice(0, 12)}…`

  return (
    <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
      {item.destroyed ? (
        <EmptyRow className="bg-strip">
          The file was destroyed
          {item.destroyedAt && (
            <>
              {' '}on <span className="font-mono">{formatDay(item.destroyedAt)}</span>
            </>
          )}
          {item.destroyedBy ? ` by ${item.destroyedBy}` : ''}.{item.destroyedReason ? ` ${item.destroyedReason}` : ''}
        </EmptyRow>
      ) : item.contentType.startsWith('video/') ? (
        <video controls preload="metadata" src={api.evidenceUrl(item.hash)} className="max-h-80 w-full bg-black" />
      ) : (
        <Picture item={item} onImageReady={onImageReady} />
      )}

      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="truncate font-medium" title={name}>
          {name}
        </span>
        <span className="text-muted-foreground">
          {item.contentType} · <span className="font-mono">{bytes(item.byteSize)}</span> ·{' '}
          <span className="font-mono">{formatDay(item.firstStoredAt)}</span>
          {item.uploaderId ? ` · by ${item.uploaderId}` : ''}
          {item.origin === 'Captured' ? ' · captured by Modbot' : ''}
        </span>
        {!item.destroyed && (
          <a href={api.evidenceUrl(item.hash)} className="underline underline-offset-2" download>
            Download
          </a>
        )}
      </div>
      <div className="truncate font-mono text-muted-foreground/70" style={{ fontSize: 'var(--text-tiny)' }} title={item.hash}>
        sha256 {item.hash}
      </div>
    </div>
  )
}

/** An image, fetched with credentials and shown from a typed object URL. */
function Picture({ item, onImageReady }: { item: EvidenceItem; onImageReady?: (hash: string, url: string) => void }) {
  const [url, setUrl] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    let objectUrl: string | null = null
    let cancelled = false

    fetch(api.evidenceUrl(item.hash), { credentials: 'same-origin' })
      .then(async (response) => {
        if (!response.ok) {
          const text = await response.text().catch(() => '')
          let message = `The server answered ${response.status}.`
          try {
            const body: unknown = text ? JSON.parse(text) : null
            if (typeof body === 'object' && body !== null && 'error' in body) message = String((body as { error: unknown }).error)
          } catch {
            // Not JSON; the status is all there is to say.
          }
          throw new Error(message)
        }
        return response.arrayBuffer()
      })
      .then((buffer) => {
        if (cancelled) return
        // Typed from the blob record, never from the response: the object URL cannot be HTML.
        objectUrl = URL.createObjectURL(new Blob([buffer], { type: item.contentType }))
        setUrl(objectUrl)
        onImageReady?.(item.hash, objectUrl)
      })
      .catch((e: unknown) => {
        if (!cancelled) setProblem(e instanceof Error ? e.message : 'Could not load this image.')
      })

    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [item.hash, item.contentType, onImageReady])

  if (problem) {
    return (
      <EmptyRow className="bg-strip">
        Could not show this image: {problem}{' '}
        <a href={api.evidenceUrl(item.hash)} className="underline underline-offset-2">
          Download
        </a>
      </EmptyRow>
    )
  }

  if (!url) return <div className="h-40 animate-pulse bg-muted" aria-label="Loading image" />

  return (
    <a href={url} target="_blank" rel="noopener noreferrer" title="Open full size in a new tab">
      <img src={url} alt={item.fileName ?? ''} className="max-h-80 w-full object-contain" />
    </a>
  )
}

type Progress =
  | { phase: 'idle' }
  | { phase: 'hashing' }
  | { phase: 'starting' }
  | { phase: 'sending'; sent: number; total: number; bytesPerSecond: number }
  | { phase: 'committing' }
  | { phase: 'done'; name: string }
  | { phase: 'failed'; message: string }

function Attach({ caseId, delivery, onChanged }: { caseId: string; delivery: EvidenceDelivery; onChanged: () => void }) {
  const [progress, setProgress] = useState<Progress>({ phase: 'idle' })
  const input = useRef<HTMLInputElement>(null)
  const busy = !['idle', 'done', 'failed'].includes(progress.phase)

  const upload = async (file: File) => {
    if (delivery.maxFileBytes > 0 && file.size > delivery.maxFileBytes) {
      setProgress({ phase: 'failed', message: `${file.name} is ${bytes(file.size)}; the limit is ${bytes(delivery.maxFileBytes)} per file.` })
      return
    }

    try {
      // The hash is a claim the server checks against what it actually stored. Skipped for
      // very large files rather than holding them whole in memory twice.
      let expectedHash: string | null = null
      if (file.size <= 64 * 1024 * 1024 && typeof crypto?.subtle?.digest === 'function') {
        setProgress({ phase: 'hashing' })
        const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
        expectedHash = Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('')
      }

      setProgress({ phase: 'starting' })
      const ticket = await api.beginEvidenceUpload({
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        length: file.size,
        reportId: caseId,
      })

      await transfer(ticket.transferUrl, ticket.presigned, file, (sent, total, bytesPerSecond) =>
        setProgress({ phase: 'sending', sent, total, bytesPerSecond }),
      )

      setProgress({ phase: 'committing' })
      await api.commitEvidenceUpload(ticket.uploadId, expectedHash)

      setProgress({ phase: 'done', name: file.name })
      onChanged()
    } catch (e: unknown) {
      setProgress({
        phase: 'failed',
        message:
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to upload evidence.'
            : e instanceof Error
              ? e.message
              : 'The upload failed.',
      })
    } finally {
      if (input.current) input.current.value = ''
    }
  }

  return (
    <CardFooter className="flex-wrap gap-x-2 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      <input
        ref={input}
        type="file"
        accept={delivery.acceptedTypes.join(',')}
        disabled={busy || !delivery.uploadsAllowed}
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) void upload(file)
        }}
        className="hidden"
        id={`attach-${caseId}`}
      />
      <Button asChild size="xs" variant="outline" disabled={busy || !delivery.uploadsAllowed}>
        <label htmlFor={`attach-${caseId}`} className={busy || !delivery.uploadsAllowed ? 'pointer-events-none opacity-50' : 'cursor-pointer'}>
          {busy ? 'Uploading…' : 'Attach a screenshot or video'}
        </label>
      </Button>
      <span className="text-muted-foreground">
        {delivery.acceptedTypes.join(', ')}
        {delivery.maxFileBytes > 0 ? ` · up to ${bytes(delivery.maxFileBytes)} each` : ''}
      </span>

      {/* The refusal and the upload's progress each take a line of their own under the button, and
          only while there is something to say, so the strip at rest is one control high. */}
      {!delivery.uploadsAllowed && (
        <p className="basis-full text-warn">Uploads are refused right now: {delivery.storeExplanation}</p>
      )}

      {progress.phase !== 'idle' && (
        <div className="basis-full">
          <ProgressLine progress={progress} />
        </div>
      )}
    </CardFooter>
  )
}

function ProgressLine({ progress }: { progress: Progress }) {
  if (progress.phase === 'idle') return null

  const small = { fontSize: 'var(--text-small)' } as const

  switch (progress.phase) {
    case 'hashing':
      return <p className="text-muted-foreground" style={small}>Checking the file…</p>
    case 'starting':
      return <p className="text-muted-foreground" style={small}>Starting…</p>
    case 'sending': {
      const fraction = progress.total > 0 ? progress.sent / progress.total : 0
      const rate = progress.bytesPerSecond
      return (
        <div className="flex flex-col gap-1" style={small} aria-live="polite">
          <div className="h-1.5 w-full overflow-hidden bg-muted">
            <div className="h-full bg-primary transition-[width]" style={{ width: `${Math.round(fraction * 100)}%` }} />
          </div>
          <span className="font-mono text-muted-foreground">
            {bytes(progress.sent)} of {bytes(progress.total)}
            {rate > 0 ? ` · ${bytes(rate)}/s` : ''}
          </span>
        </div>
      )
    }
    case 'committing':
      return <p className="text-muted-foreground" style={small}>Attaching…</p>
    case 'done':
      return <p className="text-ok" style={small}>Attached {progress.name}.</p>
    case 'failed':
      return <p className="text-destructive" style={small}>{progress.message}</p>
  }
}

/**
 * Phase 2: the file as a raw body, with progress.
 *
 * XMLHttpRequest rather than fetch because fetch has no upload progress, and a hundred megabytes
 * on a home connection is minutes -- long enough that a bar which does not move gets cancelled
 * by an impatient human (evidence design §9.6). A presigned target is another origin and gets no
 * cookie; Modbot's own endpoint gets the session.
 */
function transfer(
  url: string,
  presigned: boolean,
  file: File,
  onProgress: (sent: number, total: number, bytesPerSecond: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    const startedAt = Date.now()

    // Measured here rather than in the component: the clock is read when a progress event
    // arrives, which is an event, not a render.
    const rate = (sent: number) => {
      const elapsed = (Date.now() - startedAt) / 1000
      return elapsed > 0.5 ? sent / elapsed : 0
    }

    xhr.open('PUT', url)
    xhr.withCredentials = !presigned
    if (presigned && file.type) xhr.setRequestHeader('Content-Type', file.type)
    else if (!presigned) xhr.setRequestHeader('Content-Type', 'application/octet-stream')

    xhr.upload.onprogress = (e) =>
      onProgress(e.loaded, e.lengthComputable ? e.total : file.size, rate(e.loaded))
    xhr.onerror = () => reject(new Error('The connection dropped. Try again.'))
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress(file.size, file.size, rate(file.size))
        resolve()
        return
      }

      let message = `Sending the file failed (${xhr.status}).`
      try {
        const body: unknown = xhr.responseText ? JSON.parse(xhr.responseText) : null
        if (typeof body === 'object' && body !== null && 'error' in body) message = String((body as { error: unknown }).error)
      } catch {
        // A bucket answers in XML; the status is the message then.
      }
      reject(new Error(message))
    }

    xhr.send(file)
  })
}
