import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { PanelGrid } from '@/components/PanelGrid'
import { Row } from '@/components/settings/fields'
import credits from '@/lib/credits.json'
import { DOCS_URL } from '@/lib/docs'
import { myModbotOrigin } from '@/lib/myModbot'

/** Who made Modbot, where to find them, and the documents that say what Modbot is allowed to do. */

const REPO = credits.modbot.url

export function General() {
  return (
    <PanelGrid className="grid-cols-1">
      <Card>
        <CardHeader>
          <CardTitle>
            <h2 className="flex items-center gap-2">
              {credits.modbot.name}
              <Badge variant="secondary" className="font-mono font-normal">
                {credits.modbot.licence}
              </Badge>
            </h2>
          </CardTitle>
        </CardHeader>
        <CardContent>
          <Row label="Source" value={<Link href={REPO}>{REPO.replace('https://', '')}</Link>} />
          <Row label="Licence" value={<Link href={`${REPO}/blob/master/LICENSE`}>AGPL-3.0</Link>} />
          <Row
            label="Privacy policy"
            value={<Link href={`${REPO}/blob/master/PRIVACY_POLICY.md`}>PRIVACY_POLICY.md</Link>}
          />
          <Row label="Documentation" value={<Link href={DOCS_URL}>{DOCS_URL.replace('https://', '')}</Link>} />
          <Row
            label="Your servers"
            value={<Link href={myModbotOrigin()}>{myModbotOrigin().replace('https://', '')}</Link>}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>
            <h2>Made by</h2>
          </CardTitle>
        </CardHeader>
        <CardContent>
          <Row label="Name" value="Sarmad Wahab" />
          <Row label="Username" value="bin" />
          <Row label="Website" value={<Link href="https://bin.moe">bin.moe</Link>} />
          <Row label="GitHub" value={<Link href="https://github.com/binn">github.com/binn</Link>} />
        </CardContent>
      </Card>
    </PanelGrid>
  )
}

function Link({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a href={href} target="_blank" rel="noreferrer" className="hover:underline">
      {children}
    </a>
  )
}
