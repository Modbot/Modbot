import { useEffect, useRef } from 'react'
import { api } from '@/lib/api'
import { normaliseInstanceUrl } from '@/lib/instanceUrl'
import { useLocation } from '@/lib/router'
import { Admin } from '@/pages/admin/Admin'
import { Go } from '@/pages/Go'
import { Home } from '@/pages/Home'
import { NotFound } from '@/pages/NotFound'
import { Register } from '@/pages/Register'

/** The routes that record an instance URL carried in `url`. The server records it on load too. */
const RECORDING_ROUTES = new Set(['/', '/register', '/go'])

export default function App() {
  const { path, search } = useLocation()

  const url = RECORDING_ROUTES.has(path) ? normaliseInstanceUrl(search.get('url')) : null
  const recordedFor = useRef<string | null>(null)

  // The second save (central services spec 4.1): once rendered, whether the page came from the
  // server, the browser's cache or a navigation inside the app. The server collapses it into the
  // visit the page load already counted. Guarded per route and URL so a re-render does not send it
  // again, and cleared on leaving so coming back does.
  useEffect(() => {
    const key = url ? `${path} ${url}` : null
    if (key === recordedFor.current) return
    recordedFor.current = key
    if (url) api.localRegister(url).catch(() => {})
  }, [path, url])

  if (path === '/') return <Home />
  if (path === '/register') return <Register key={search.get('url') ?? ''} url={search.get('url')} />
  if (path === '/go') return <Go key={search.get('redir') ?? ''} redir={search.get('redir')} />
  if (path === '/admin' || path.startsWith('/admin/')) return <Admin path={path} />
  return <NotFound />
}
