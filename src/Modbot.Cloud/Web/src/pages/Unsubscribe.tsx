import { useEffect, useState } from 'react'
import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ApiError, api } from '@/lib/api'
import { Done, Problem } from './account/Forms'

/**
 * Where the unsubscribe link at the foot of a Modbot message lands.
 *
 * It needs no account and asks nothing: the token in the link is the whole of the proof, and a
 * person who wants out of a mailing list should be out of it by the time the page has loaded. The
 * work is a POST, so a mail scanner that fetches every link in a message takes nobody off the list.
 */
export function Unsubscribe({ token }: { token: string | null }) {
  const [state, setState] = useState<'working' | 'done' | 'failed'>(token ? 'working' : 'failed')
  const [error, setError] = useState<string | null>(token ? null : 'That link is missing its code.')

  useEffect(() => {
    if (!token) return

    api
      .unsubscribe(token)
      .then(() => setState('done'))
      .catch((failure: unknown) => {
        setState('failed')
        setError(failure instanceof ApiError ? failure.message : 'Could not reach the server.')
      })
  }, [token])

  return (
    <Shell>
      <Card className="flex flex-col gap-4 px-6">
        <h1 className="font-display text-base">Modbot emails</h1>
        {state === 'working' && <Done>Working…</Done>}
        {state === 'done' && <Done>Unsubscribed.</Done>}
        <Problem>{state === 'failed' && error}</Problem>
        <div>
          <Button asChild variant="outline">
            <Link href="/">Modbot Cloud</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}
