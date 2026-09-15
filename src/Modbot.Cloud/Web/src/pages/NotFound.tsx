import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

export function NotFound() {
  return (
    <Shell>
      <Card className="gap-4 px-6">
        <h1 className="font-display text-base">Page not found</h1>
        <div>
          <Button asChild variant="outline">
            <Link href="/">Home</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}
