import { api, ApiError, type EvidenceDelivery } from '@/lib/api'
import { bytes } from '@/components/settings/units'

/**
 * Sending one file of evidence: the three phases of evidence design §9.1, split where a screen
 * may need to wait between them.
 *
 * The case file page sends and commits in one go. The ban dialog cannot: the moderator picks the
 * screenshot while they are still typing, and the case file it belongs to is only written once
 * VRChat has accepted the ban. So `send` begins with no case file and moves the bytes, and
 * `attach` names the case file at commit, once there is one. Nothing is attached until then; a
 * dialog closed without banning leaves a staging object, which the sweep takes.
 */
export type Progress =
  | { phase: 'idle' }
  | { phase: 'hashing' }
  | { phase: 'starting' }
  | { phase: 'sending'; sent: number; total: number; bytesPerSecond: number }
  | { phase: 'ready' }
  | { phase: 'committing' }
  | { phase: 'done'; name: string }
  | { phase: 'failed'; message: string }

/** A file whose bytes are in the store, waiting to be attached. */
export type Sent = { uploadId: string; expectedHash: string | null; name: string }

/** Whether a progress is still moving, so a control that waits on it knows to keep waiting. */
export function busy(progress: Progress): boolean {
  return !['idle', 'ready', 'done', 'failed'].includes(progress.phase)
}

/** The refusal to show before a byte moves, or null when the file may be sent. */
export function tooLarge(file: File, delivery: EvidenceDelivery): string | null {
  return delivery.maxFileBytes > 0 && file.size > delivery.maxFileBytes
    ? `${file.name} is ${bytes(file.size)}; the limit is ${bytes(delivery.maxFileBytes)} per file.`
    : null
}

/** Phases 1 and 2: begin, then the bytes. `reportId` may be null when the case file is not written yet. */
export async function send(
  file: File,
  reportId: string | null,
  onProgress: (progress: Progress) => void,
): Promise<Sent> {
  // The hash is a claim the server checks against what it actually stored. Skipped for very large
  // files rather than holding them whole in memory twice.
  let expectedHash: string | null = null
  if (file.size <= 64 * 1024 * 1024 && typeof crypto?.subtle?.digest === 'function') {
    onProgress({ phase: 'hashing' })
    const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
    expectedHash = Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('')
  }

  onProgress({ phase: 'starting' })
  const ticket = await api.beginEvidenceUpload({
    fileName: file.name,
    contentType: file.type || 'application/octet-stream',
    length: file.size,
    reportId,
  })

  await transfer(ticket.transferUrl, ticket.presigned, file, (sent, total, bytesPerSecond) =>
    onProgress({ phase: 'sending', sent, total, bytesPerSecond }),
  )

  onProgress({ phase: 'ready' })
  return { uploadId: ticket.uploadId, expectedHash, name: file.name }
}

/** Phase 3: commit, naming the case file, and the saved clip the file is when it is one. */
export async function attach(
  sent: Sent,
  reportId: string,
  onProgress: (progress: Progress) => void,
  clipId: number | null = null,
): Promise<void> {
  onProgress({ phase: 'committing' })
  await api.commitEvidenceUpload(sent.uploadId, sent.expectedHash, reportId, clipId)
  onProgress({ phase: 'done', name: sent.name })
}

/** What a failed upload says. */
export function failure(e: unknown): Progress {
  return {
    phase: 'failed',
    message:
      e instanceof ApiError && e.status === 403
        ? 'You do not have permission to upload evidence.'
        : e instanceof Error
          ? e.message
          : 'The upload failed.',
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
