import { useLocation } from '@/lib/router'
import { Account } from '@/pages/account/Account'
import { ForgotPassword, Register, SignIn } from '@/pages/account/SignIn'
import { ResetPassword, Verify } from '@/pages/account/Tokens'
import { Admin } from '@/pages/admin/Admin'
import { Home } from '@/pages/Home'
import { NotFound } from '@/pages/NotFound'
import { Unsubscribe } from '@/pages/Unsubscribe'

export default function App() {
  const { path, search } = useLocation()

  if (path === '/') return <Home />
  if (path === '/admin' || path.startsWith('/admin/')) return <Admin path={path} />

  if (path === '/register') return <Register />
  if (path === '/sign-in') return <SignIn />
  if (path === '/forgot-password') return <ForgotPassword />
  if (path === '/account') return <Account />

  // The two routes the links in Cloud's mail land on.
  if (path === '/verify') return <Verify token={search.get('token')} change={false} />
  if (path === '/verify-email-change') return <Verify token={search.get('token')} change={true} />
  if (path === '/reset-password') return <ResetPassword token={search.get('token')} />

  // Not an account page: the link at the foot of a Modbot mailing-list message.
  if (path === '/unsubscribe') return <Unsubscribe token={search.get('token')} />

  return <NotFound />
}
