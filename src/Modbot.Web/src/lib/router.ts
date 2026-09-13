import { useCallback, useEffect, useMemo, useState } from 'react'

/**
 * The whole router.
 *
 * Modbot's SPA has two route spaces that matter: the setup wizard, which lives outside the app
 * shell, and everything inside it. That is a `pathname` and a `pushState` away from working, and
 * a routing library would be a dependency, a bundle, and a set of conventions in exchange for
 * the sixty lines below. When the shell grows real nested routes this is the thing to replace --
 * until then it is the thing not to have added.
 *
 * Path-based rather than hash-based because the host serves index.html for unknown paths, so
 * /audit survives a refresh and can be linked to.
 *
 * It tracks the query string as well as the path, because the subject pane is deep-linkable
 * (`?subject=usr_…`, spec 10.2). A pane that cannot be linked is a pane nobody shares -- and a
 * router that ignored the query would leave the URL changing while the page did not, which is the
 * single most common way a hand-rolled router is broken.
 */
function currentLocation() {
  return window.location.pathname + window.location.search
}

export function useLocation(): [
  { path: string; search: URLSearchParams },
  (to: string, options?: { replace?: boolean }) => void,
] {
  const [href, setHref] = useState(currentLocation)

  useEffect(() => {
    // Back and forward buttons.
    const onPopState = () => setHref(currentLocation())
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])

  const navigate = useCallback((to: string, options?: { replace?: boolean }) => {
    if (to === currentLocation()) return

    if (options?.replace) window.history.replaceState(null, '', to)
    else window.history.pushState(null, '', to)

    setHref(currentLocation())
  }, [])

  const location = useMemo(() => {
    const query = href.indexOf('?')
    return {
      path: query < 0 ? href : href.slice(0, query),
      search: new URLSearchParams(query < 0 ? '' : href.slice(query)),
    }
  }, [href])

  return [location, navigate]
}

/** The path alone, for callers that do not care about the query string. */
export function useRoute(): [string, (to: string, options?: { replace?: boolean }) => void] {
  const [location, navigate] = useLocation()
  return [location.path, navigate]
}

/**
 * One query parameter, read and written without disturbing the rest of the URL.
 *
 * Setting it replaces rather than pushes: opening and closing the subject pane a dozen times
 * while scanning a log should not bury the page the moderator came from under a dozen history
 * entries. The link still works when pasted, which is the property that matters.
 */
export function useQueryParam(
  name: string,
): [string | null, (value: string | null) => void] {
  const [location, navigate] = useLocation()
  const value = location.search.get(name)

  const set = useCallback(
    (next: string | null) => {
      const params = new URLSearchParams(window.location.search)

      if (next === null) params.delete(name)
      else params.set(name, next)

      const query = params.toString()
      navigate(window.location.pathname + (query ? `?${query}` : ''), { replace: true })
    },
    [name, navigate],
  )

  return [value, set]
}
