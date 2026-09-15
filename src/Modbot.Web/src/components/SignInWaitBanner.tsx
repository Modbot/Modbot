import { useEffect, useState } from 'react'
import { signInWaitText } from '@/lib/signInWait'
import { useGateHealth } from '@/lib/useGateHealth'

/**
 * The red banner shown while Modbot waits to sign in to VRChat (foundation spec 4.1.2).
 *
 * On every page of the app shell and the setup wizard, for anyone signed in. It has no close
 * button on purpose: it stays for exactly as long as the server says Modbot is waiting, and goes
 * the moment it says otherwise.
 *
 * The countdown starts from the seconds the server counted on its own clock and subtracts the time
 * since that answer arrived, measured with `performance.now()` -- so a browser whose clock is wrong
 * still shows the right number.
 */
export function SignInWaitBanner() {
  const { gate, receivedAt } = useGateHealth()
  const wait = gate?.signInWait ?? null

  const [now, setNow] = useState(() => performance.now())

  useEffect(() => {
    if (!wait) return
    const timer = setInterval(() => setNow(performance.now()), 1000)
    return () => clearInterval(timer)
  }, [wait])

  if (!wait) return null

  const left = wait.secondsLeft - Math.max(0, now - receivedAt) / 1000

  return (
    <div
      role="alert"
      className="w-full bg-destructive px-5 py-2 text-center font-medium text-destructive-foreground"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {signInWaitText(left)}
    </div>
  )
}
