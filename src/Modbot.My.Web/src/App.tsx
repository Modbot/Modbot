import { useEffect, useRef } from 'react'
import { normaliseInstanceUrl } from '@/lib/instanceUrl'
import { outbox } from '@/lib/outbox'
import { useLocation } from '@/lib/router'
import { detailsFromLink } from '@/lib/serverDetails'
import { Go } from '@/pages/Go'
import { Home } from '@/pages/Home'
import { NotFound } from '@/pages/NotFound'
import { Register } from '@/pages/Register'

/** The routes that note an instance address carried in `url`. The server notes it on load too. */
const RECORDING_ROUTES = new Set(['/', '/register', '/go'])

export default function App() {
  const { path, search } = useLocation()

  const url = RECORDING_ROUTES.has(path) ? normaliseInstanceUrl(search.get('url')) : null
  const recordedFor = useRef<string | null>(null)

  // Anything an earlier page load could not send goes first.
  useEffect(() => outbox.start(), [])

  // The second save (central services spec 4.1): once rendered, whether the page came from the
  // server, the browser's cache or a navigation inside the app. The server collapses it into the
  // visit the page load already counted. Guarded per route and URL so a re-render does not send it
  // again, and cleared on leaving so coming back does. It goes through the outbox, so a server that
  // cannot take it now gets it later rather than never.
  useEffect(() => {
    const key = url ? `${path} ${url}` : null
    if (key === recordedFor.current) return
    recordedFor.current = key
    if (url) outbox.add(url)
  }, [path, url])

  if (path === '/') return <Home />
  if (path === '/register')
    return <Register key={search.get('url') ?? ''} url={search.get('url')} hints={detailsFromLink(search)} />
  if (path === '/go') return <Go key={search.get('redir') ?? ''} redir={search.get('redir')} />
  return <NotFound />
}
