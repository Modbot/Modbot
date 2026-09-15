/** "just now", "5 min ago", "3 days ago", or a date once it is further back than that. */
export function ago(iso: string): string {
  const then = Date.parse(iso)
  if (Number.isNaN(then) || then <= 0) return ''

  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000))
  if (seconds < 60) return 'just now'

  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`

  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`

  const days = Math.round(hours / 24)
  if (days < 60) return days === 1 ? '1 day ago' : `${days} days ago`

  return new Date(then).toLocaleDateString()
}

export function when(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '—'
}

export function host(url: string): string {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}
