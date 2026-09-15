import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

export function NotFound() {
  return (
    <Shell>
      <Card className="gap-4 px-6">
        <h1 className="text-base font-display">Page not found</h1>
        <div>
          <Button asChild variant="outline">
            <Link href="/">Your instances</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}
