import { useCallback, useEffect, useState } from 'react'

/**
 * The whole router.
 *
 * Modbot's SPA has two route spaces that matter: the setup wizard, which lives outside the app
 * shell, and everything inside it. That is a `pathname` and a `pushState` away from working, and
 * a routing library would be a dependency, a bundle, and a set of conventions in exchange for
 * the twenty lines below. When the shell grows real nested routes this is the thing to replace --
 * until then it is the thing not to have added.
 *
 * Path-based rather than hash-based because the host serves index.html for unknown paths, so
 * /setup survives a refresh and can be linked to.
 */
export function useRoute(): [string, (to: string, options?: { replace?: boolean }) => void] {
  const [path, setPath] = useState(() => window.location.pathname)

  useEffect(() => {
    // Back and forward buttons. Without this the URL changes and the page does not, which is the
    // single most common way a hand-rolled router is broken.
    const onPopState = () => setPath(window.location.pathname)
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])

  const navigate = useCallback((to: string, options?: { replace?: boolean }) => {
    if (to === window.location.pathname) return

    if (options?.replace) window.history.replaceState(null, '', to)
    else window.history.pushState(null, '', to)

    setPath(to)
  }, [])

  return [path, navigate]
}
