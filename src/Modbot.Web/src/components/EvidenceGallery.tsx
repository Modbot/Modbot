import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { CardFooter } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { api, ApiError, type EvidenceClip, type EvidenceDelivery, type EvidenceItem, type SavedClip } from '@/lib/api'
import { attach, busy, failure, send, tooLarge, type Progress } from '@/lib/evidenceUpload'
import { dateTime, formatDay } from '@/lib/format'
import { instanceName } from '@/lib/instanceName'
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
 *
 * A file can sit on several case files. **Take off** ends this case file's hold and leaves a line
 * saying it was there; the file stays for every other case file. **Destroy** takes it off this
 * case file and deletes the bytes, and the server refuses while another case file holds it. Every
 * request for the bytes names this case file (`case`), and the pictures and the video, which a
 * page shows, say so (`view`), so the audit log tells a look from a download.
 *
 * Under the files, the clips a moderator's companion said it saved while this person was there,
 * that are not on the case file yet (clips design spec §16). The server has only each clip's
 * fingerprint; Attach opens the file picker, and the server takes the file only if its bytes are
 * that clip's. A matched file is kept as captured, and its line says where and when it was saved.
 */
export function EvidenceGallery({
  caseId,
  items,
  delivery,
  canAttach,
  canDestroy,
  clips = [],
  onChanged,
  onImageReady,
}: {
  caseId: string
  items: EvidenceItem[]
  delivery: EvidenceDelivery
  canAttach: boolean
  /** Whether Destroy shows: the person may destroy evidence, on any case file, withdrawn or not. */
  canDestroy: boolean
  /** Saved clips this person may be in, not on the case file yet. */
  clips?: SavedClip[]
  onChanged: () => void
  /** Tells the page an image's object URL, so the written reason can show `evidence:` references inline. */
  onImageReady?: (hash: string, url: string) => void
}) {
  // What the case file holds, and a trace of what it let go of.
  const held = items.filter((item) => !item.takenOffAt)
  const off = items.filter((item) => item.takenOffAt)

  // What a take-off or destroy that failed part way says. The page is read again at once, because
  // the take-off may have happened and the file is no longer held: the file's line moves, and this
  // is where the sentence waits for whoever pressed the button.
  const [notice, setNotice] = useState<string | null>(null)
  const changed = () => {
    setNotice(null)
    onChanged()
  }
  const failed = (message: string) => {
    setNotice(message)
    onChanged()
  }

  return (
    <div className="flex flex-col">
      {notice && (
        <p className="px-(--panel-pad) py-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {notice}
        </p>
      )}

      {held.length === 0 && <EmptyRow>Nothing attached.</EmptyRow>}

      {held.length > 0 && (
        // Edge to edge in its panel, so each item's lines are the panel's own. Two across only when
        // there are two to put side by side; a lone item takes the width.
        <PanelGrid as="ul" className={cn('m-0', held.length > 1 && 'sm:grid-cols-2')}>
          {held.map((item) => (
            <li key={item.hash} className="p-(--panel-pad)">
              <Item
                item={item}
                caseId={caseId}
                canTakeOff={canAttach}
                canDestroy={canDestroy}
                onChanged={changed}
                onFailed={failed}
                onImageReady={onImageReady}
              />
            </li>
          ))}
        </PanelGrid>
      )}

      {off.length > 0 && (
        <PanelGrid as="ul" className="m-0">
          {off.map((item) => (
            <li key={`${item.hash}-${item.takenOffAt}`} className="px-(--panel-pad) py-2">
              <Trace item={item} caseId={caseId} canDestroy={canDestroy} onChanged={changed} onFailed={failed} />
            </li>
          ))}
        </PanelGrid>
      )}

      {clips.length > 0 && (
        <PanelGrid as="ul" className="m-0">
          {clips.map((clip) => (
            <li key={clip.id} className="px-(--panel-pad) py-2">
              <ClipRow clip={clip} caseId={caseId} delivery={delivery} canAttach={canAttach} onChanged={changed} />
            </li>
          ))}
        </PanelGrid>
      )}

      {canAttach && <Attach caseId={caseId} delivery={delivery} onChanged={changed} />}
    </div>
  )
}

/** Who saved a clip, by name where their companion gave one. */
function savedBy(clip: { savedBy: string | null; savedById: string | null }): string {
  return clip.savedBy || clip.savedById || 'a moderator'
}

/**
 * One saved clip that is not on the case file: where and when, whose PC, and Attach. The picker
 * takes any file; the server refuses one whose bytes are not this clip, in a sentence that says so.
 */
function ClipRow({
  clip,
  caseId,
  delivery,
  canAttach,
  onChanged,
}: {
  clip: SavedClip
  caseId: string
  delivery: EvidenceDelivery
  canAttach: boolean
  onChanged: () => void
}) {
  const [progress, setProgress] = useState<Progress>({ phase: 'idle' })
  const input = useRef<HTMLInputElement>(null)
  const sending = busy(progress)

  const upload = async (file: File) => {
    const refusal = tooLarge(file, delivery)
    if (refusal) {
      setProgress({ phase: 'failed', message: refusal })
      return
    }

    try {
      const sent = await send(file, caseId, setProgress)
      await attach(sent, caseId, setProgress, clip.id)
      onChanged()
    } catch (e: unknown) {
      setProgress(failure(e))
    } finally {
      if (input.current) input.current.value = ''
    }
  }

  return (
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      <span>
        Clip saved on {savedBy(clip)}'s PC at <span className="font-mono">{dateTime(clip.savedAt)}</span>
      </span>
      <span className="text-muted-foreground">
        {instanceName(clip.worldName, clip.worldId, clip.instanceId)} · <span className="font-mono">{bytes(clip.byteSize)}</span> ·
        not uploaded
      </span>
      {canAttach && (
        <>
          <input
            ref={input}
            type="file"
            accept={delivery.acceptedTypes.join(',')}
            disabled={sending || !delivery.uploadsAllowed}
            tabIndex={-1}
            aria-hidden
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void upload(file)
            }}
            className="hidden"
          />
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={sending || !delivery.uploadsAllowed}
            onClick={() => input.current?.click()}
          >
            {sending ? 'Uploading…' : 'Attach'}
          </Button>
        </>
      )}
      {progress.phase !== 'idle' && (
        <div className="basis-full">
          <ProgressLine progress={progress} />
        </div>
      )}
    </div>
  )
}

/** What an evidence line says about a file that is a saved clip. */
function clipWords(clip: EvidenceClip): string {
  return `clip saved on ${savedBy(clip)}'s PC, ${dateTime(clip.savedAt)}, ${instanceName(clip.worldName, clip.worldId, clip.instanceId)}`
}

function Item({
  item,
  caseId,
  canTakeOff,
  canDestroy,
  onChanged,
  onFailed,
  onImageReady,
}: {
  item: EvidenceItem
  caseId: string
  canTakeOff: boolean
  canDestroy: boolean
  onChanged: () => void
  onFailed: (message: string) => void
  onImageReady?: (hash: string, url: string) => void
}) {
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
        <video
          controls
          preload="metadata"
          src={api.evidenceUrl(item.hash, { caseId, view: true })}
          className="max-h-80 w-full bg-black"
        />
      ) : (
        <Picture item={item} caseId={caseId} onImageReady={onImageReady} />
      )}

      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="truncate font-medium" title={name}>
          {name}
        </span>
        <span className="text-muted-foreground">
          {item.contentType} · <span className="font-mono">{bytes(item.byteSize)}</span> ·{' '}
          <span className="font-mono">{formatDay(item.firstStoredAt)}</span>
          {item.uploaderId ? ` · by ${item.uploaderId}` : ''}
          {item.clip ? ` · ${clipWords(item.clip)}` : item.origin === 'Captured' ? ' · captured by Modbot' : ''}
        </span>
        {!item.destroyed && (
          <a href={api.evidenceUrl(item.hash, { caseId })} className="underline underline-offset-2" download>
            Download
          </a>
        )}
        <Actions
          item={item}
          caseId={caseId}
          canTakeOff={canTakeOff && !item.destroyed}
          canDestroy={canDestroy && !item.destroyed}
          onChanged={onChanged}
          onFailed={onFailed}
        />
      </div>
      <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }} title={item.hash}>
        sha256 {item.hash}
      </div>
    </div>
  )
}

/**
 * The one line a file leaves behind when it is taken off a case file: what it was, who took it
 * off and when. If the bytes were destroyed since, it says that too.
 */
function Trace({
  item,
  caseId,
  canDestroy,
  onChanged,
  onFailed,
}: {
  item: EvidenceItem
  caseId: string
  canDestroy: boolean
  onChanged: () => void
  onFailed: (message: string) => void
}) {
  const name = item.fileName ?? `${item.hash.slice(0, 12)}…`

  return (
    <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="truncate font-medium" title={name}>
          {name}
        </span>
        <span className="text-muted-foreground">
          taken off{item.takenOffBy ? ` by ${item.takenOffBy}` : ''}
          {item.takenOffAt && (
            <>
              {', '}
              <span className="font-mono">{formatDay(item.takenOffAt)}</span>
            </>
          )}
          {item.destroyed && (
            <>
              {' · destroyed'}
              {item.destroyedBy ? ` by ${item.destroyedBy}` : ''}
              {item.destroyedAt && (
                <>
                  {', '}
                  <span className="font-mono">{formatDay(item.destroyedAt)}</span>
                </>
              )}
              {item.destroyedReason ? `: ${item.destroyedReason}` : ''}
            </>
          )}
        </span>
        <Actions
          item={item}
          caseId={caseId}
          canTakeOff={false}
          canDestroy={canDestroy && !item.destroyed}
          onChanged={onChanged}
          onFailed={onFailed}
        />
      </div>
    </div>
  )
}

/**
 * Take off and Destroy, each asking before it does anything.
 *
 * Take off asks only whether. Destroy asks for the reason, which is written into the log beside who
 * did it, and has no way back. A refusal -- the file is still on another case file, the store is
 * down -- is the server's own sentence, shown here and nothing is reloaded.
 */
function Actions({
  item,
  caseId,
  canTakeOff,
  canDestroy,
  onChanged,
  onFailed,
}: {
  item: EvidenceItem
  caseId: string
  canTakeOff: boolean
  canDestroy: boolean
  onChanged: () => void
  /** The server errored, so what happened is not known: the sentence goes up to the gallery and the page is read again. */
  onFailed: (message: string) => void
}) {
  const [mode, setMode] = useState<'idle' | 'takeOff' | 'destroy'>('idle')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  if (!canTakeOff && !canDestroy) return null

  const close = () => {
    setMode('idle')
    setReason('')
    setProblem(null)
  }

  const takeOff = () => {
    setBusy(true)
    setProblem(null)

    api
      .takeEvidenceOff(caseId, item.hash)
      .then(() => onChanged())
      .catch((e: unknown) => onFailed(e instanceof ApiError ? e.message : 'Could not take it off.'))
      .finally(() => setBusy(false))
  }

  const destroy = () => {
    setBusy(true)
    setProblem(null)

    api
      .destroyEvidence(item.hash, caseId, reason.trim())
      .then((result) => {
        if (result.destroyed) onChanged()
        else setProblem(result.message)
      })
      .catch((e: unknown) => onFailed(e instanceof ApiError ? e.message : 'Could not destroy it.'))
      .finally(() => setBusy(false))
  }

  if (mode === 'idle') {
    return (
      <span className="flex items-center gap-1">
        {canTakeOff && (
          <Button type="button" size="xs" variant="ghost" onClick={() => setMode('takeOff')}>
            Take off
          </Button>
        )}
        {canDestroy && (
          <Button type="button" size="xs" variant="ghost" onClick={() => setMode('destroy')}>
            Destroy
          </Button>
        )}
      </span>
    )
  }

  return (
    <div className="flex basis-full flex-wrap items-center gap-2">
      {mode === 'takeOff' ? (
        <>
          <span>Take it off this case file?</span>
          <Button type="button" size="xs" variant="outline" onClick={takeOff} disabled={busy}>
            {busy ? 'Taking off…' : 'Take off'}
          </Button>
        </>
      ) : (
        <>
          <Input
            aria-label="Why it is being destroyed"
            placeholder="Reason"
            value={reason}
            maxLength={500}
            onChange={(e) => setReason(e.target.value)}
            className="min-w-40 flex-1"
          />
          <Button type="button" size="xs" variant="destructive" onClick={destroy} disabled={busy || reason.trim().length === 0}>
            {busy ? 'Destroying…' : 'Destroy'}
          </Button>
        </>
      )}
      <Button type="button" size="xs" variant="ghost" onClick={close} disabled={busy}>
        Cancel
      </Button>
      {problem && <span className="basis-full text-destructive">{problem}</span>}
    </div>
  )
}

/** An image, fetched with credentials and shown from a typed object URL. */
function Picture({
  item,
  caseId,
  onImageReady,
}: {
  item: EvidenceItem
  caseId: string
  onImageReady?: (hash: string, url: string) => void
}) {
  const [url, setUrl] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    let objectUrl: string | null = null
    let cancelled = false

    fetch(api.evidenceUrl(item.hash, { caseId, view: true }), { credentials: 'same-origin' })
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
  }, [item.hash, item.contentType, caseId, onImageReady])

  if (problem) {
    return (
      <EmptyRow className="bg-strip">
        Could not show this image: {problem}{' '}
        <a href={api.evidenceUrl(item.hash, { caseId })} className="underline underline-offset-2">
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

function Attach({ caseId, delivery, onChanged }: { caseId: string; delivery: EvidenceDelivery; onChanged: () => void }) {
  const [progress, setProgress] = useState<Progress>({ phase: 'idle' })
  const input = useRef<HTMLInputElement>(null)
  const sending = busy(progress)

  const upload = async (file: File) => {
    const refusal = tooLarge(file, delivery)
    if (refusal) {
      setProgress({ phase: 'failed', message: refusal })
      return
    }

    try {
      const sent = await send(file, caseId, setProgress)
      await attach(sent, caseId, setProgress)
      onChanged()
    } catch (e: unknown) {
      setProgress(failure(e))
    } finally {
      if (input.current) input.current.value = ''
    }
  }

  // A real button that opens the picker, rather than a label styled as one: a label is not in the
  // tab order, so a moderator on a keyboard could not reach it at all.
  return (
    <CardFooter className="flex-wrap gap-x-2 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
      <input
        ref={input}
        type="file"
        accept={delivery.acceptedTypes.join(',')}
        disabled={sending || !delivery.uploadsAllowed}
        tabIndex={-1}
        aria-hidden
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) void upload(file)
        }}
        className="hidden"
      />
      <Button type="button" size="xs" variant="outline" disabled={sending || !delivery.uploadsAllowed} onClick={() => input.current?.click()}>
        {sending ? 'Uploading…' : 'Attach a screenshot or video'}
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

export function ProgressLine({ progress }: { progress: Progress }) {
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
    case 'ready':
      return <p className="text-muted-foreground" style={small}>Ready</p>
    case 'committing':
      return <p className="text-muted-foreground" style={small}>Attaching…</p>
    case 'done':
      return <p className="text-ok" style={small}>Attached {progress.name}.</p>
    case 'failed':
      return <p className="text-destructive" style={small}>{progress.message}</p>
  }
}
