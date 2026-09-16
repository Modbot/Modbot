import { Link } from '@/components/Link'
import { Shell } from '@/components/Shell'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

export function Home() {
  return (
    <Shell>
      <Card className="gap-4 px-6">
        <h1 className="font-display text-base">Modbot Cloud</h1>
        <div className="flex flex-wrap gap-2">
          <Button asChild>
            <Link href="/sign-in">Sign in</Link>
          </Button>
          <Button asChild variant="outline">
            <Link href="/register">Create an account</Link>
          </Button>
          <Button asChild variant="outline">
            <Link href="/admin">Admin</Link>
          </Button>
        </div>
      </Card>
    </Shell>
  )
}
