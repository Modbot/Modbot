import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import credits from '@/lib/credits.json'
import { DOCS_URL } from '@/lib/docs'
import { myModbotOrigin } from '@/lib/myModbot'

/** Who made Modbot, where to find them, and the documents that say what Modbot is allowed to do. */

const REPO = credits.modbot.url

export function General() {
  return (
    <div className="flex flex-col gap-4">
      <Card className="gap-3 py-4">
        <CardContent className="flex flex-col gap-3 px-4 sm:px-5">
          <h2 className="flex items-baseline gap-2 font-medium">
            {credits.modbot.name}
            <Badge variant="secondary" className="font-mono font-normal">
              {credits.modbot.licence}
            </Badge>
          </h2>

          <dl className="grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-[minmax(0,10rem)_minmax(0,1fr)]">
            <Line label="Source">
              <Link href={REPO}>{REPO.replace('https://', '')}</Link>
            </Line>
            <Line label="Licence">
              <Link href={`${REPO}/blob/master/LICENSE`}>AGPL-3.0</Link>
            </Line>
            <Line label="Privacy policy">
              <Link href={`${REPO}/blob/master/PRIVACY_POLICY.md`}>PRIVACY_POLICY.md</Link>
            </Line>
            <Line label="Documentation">
              <Link href={DOCS_URL}>{DOCS_URL.replace('https://', '')}</Link>
            </Line>
            <Line label="Your servers">
              <Link href={myModbotOrigin()}>{myModbotOrigin().replace('https://', '')}</Link>
            </Line>
          </dl>
        </CardContent>
      </Card>

      <Card className="gap-3 py-4">
        <CardContent className="flex flex-col gap-3 px-4 sm:px-5">
          <h2 className="font-medium">Made by</h2>

          <dl className="grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-[minmax(0,10rem)_minmax(0,1fr)]">
            <Line label="Name">Sarmad Wahab</Line>
            <Line label="Username">bin</Line>
            <Line label="Website">
              <Link href="https://bin.moe">bin.moe</Link>
            </Line>
            <Line label="GitHub">
              <Link href="https://github.com/binn">github.com/binn</Link>
            </Line>
          </dl>
        </CardContent>
      </Card>
    </div>
  )
}

function Line({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </dt>
      <dd className="pb-1 [overflow-wrap:anywhere] sm:pb-0">{children}</dd>
    </>
  )
}

function Link({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a href={href} target="_blank" rel="noreferrer" className="hover:underline">
      {children}
    </a>
  )
}
