import { Card, CardContent } from '@/components/ui/card'

export function Placeholder({ name }: { name: string }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">
        <div className="font-medium text-foreground">{name}</div>
        <p className="mt-1" style={{ fontSize: 'var(--text-small)' }}>
          Not built yet. The shell, tokens and density system are in place — pages land with their milestones.
        </p>
      </CardContent>
    </Card>
  )
}
