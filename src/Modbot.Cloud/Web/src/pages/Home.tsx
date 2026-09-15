import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

export function Home() {
  return (
    <Shell>
      <Card className="gap-4 px-6">
        <h1 className="text-base font-semibold">Modbot Cloud</h1>
        <div>
          <Button asChild variant="outline">
            <Link href="/admin">Admin</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}
