import { useEffect, useMemo, useState } from 'react'

/**
 * The whole router: the path and query string, and a way to change them.
 *
 * The app has five routes, and the server decides which paths serve it at all. A routing library
 * would be a dependency in exchange for these few lines. It follows src/Modbot.Web/src/lib/router.ts.
 */
function current() {
  return window.location.pathname + window.location.search
}

export function useLocation(): { path: string; search: URLSearchParams } {
  const [href, setHref] = useState(current)

  useEffect(() => {
    const onPopState = () => setHref(current())
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])

  return useMemo(() => {
    const query = href.indexOf('?')
    const path = query < 0 ? href : href.slice(0, query)
    return {
      path: path.length > 1 ? path.replace(/\/+$/, '') : path,
      search: new URLSearchParams(query < 0 ? '' : href.slice(query)),
    }
  }, [href])
}

/**
 * Navigates inside the app. Raising `popstate` after pushing wakes every `useLocation` at once.
 */
export function go(to: string, options?: { replace?: boolean }) {
  if (to === current()) return

  if (options?.replace) window.history.replaceState(null, '', to)
  else window.history.pushState(null, '', to)

  window.dispatchEvent(new PopStateEvent('popstate'))
}
