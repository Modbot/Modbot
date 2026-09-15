import { useLocation } from '@/lib/router'
import { Admin } from '@/pages/admin/Admin'
import { Home } from '@/pages/Home'
import { NotFound } from '@/pages/NotFound'

export default function App() {
  const { path } = useLocation()

  if (path === '/') return <Home />
  if (path === '/admin' || path.startsWith('/admin/')) return <Admin path={path} />
  return <NotFound />
}
