import { useEffect, useState } from 'react'
import { Check, X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { automaticRolesApi, type MyAutomaticRole, type MyRoleStanding } from '@/lib/automaticRoles'
import { conditionName, conditionNumbers } from '@/lib/automaticRoleStanding'

/**
 * On the account page: the automatic role rules that apply to the signed-in person, with the
 * role each gives and where they stand on every condition. Draws nothing when no rule applies,
 * and nothing while the read fails: the rest of the page is for the account and stays as it was.
 */
export function MyAutomaticRoles() {
  const [rules, setRules] = useState<MyAutomaticRole[] | null>(null)

  useEffect(() => {
    let live = true

    automaticRolesApi
      .mine()
      .then((list) => live && setRules(list.rules))
      .catch(() => undefined)

    return () => {
      live = false
    }
  }, [])

  if (!rules || rules.length === 0) return null

  return (
    <Card className="lg:col-span-2">
      <CardHeader>
        <CardTitle>Automatic roles</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
          {rules.map((rule) => (
            <li key={rule.id} className="flex flex-col gap-2">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className="min-w-0 font-medium break-words">{rule.name ?? 'Untitled rule'}</span>
                <Badge variant={rule.met ? 'ok' : 'warn'}>{rule.met ? 'Met' : 'Not met'}</Badge>
              </div>

              <div className="flex flex-wrap gap-1.5">
                {rule.vrChatRole && <RoleBadge platform="VRChat" role={rule.vrChatRole} />}
                {rule.discordRole && <RoleBadge platform="Discord" role={rule.discordRole} />}
              </div>

              <ul className="flex flex-col gap-1">
                {rule.conditions.map((condition) => {
                  const numbers = conditionNumbers(condition)

                  return (
                    <li key={condition.label} className="flex items-start gap-2">
                      {condition.met ? (
                        <Check className="mt-0.5 size-3.5 shrink-0 text-ok" aria-label="Met" />
                      ) : (
                        <X className="mt-0.5 size-3.5 shrink-0 text-destructive" aria-label="Not met" />
                      )}
                      <span className="min-w-0 break-words">
                        {conditionName(condition)}
                        {numbers && <span className="text-muted-foreground">: {numbers}</span>}
                      </span>
                    </li>
                  )
                })}
              </ul>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

function RoleBadge({ platform, role }: { platform: string; role: MyRoleStanding }) {
  return (
    <Badge variant={role.held ? 'ok' : 'secondary'}>
      {platform}: {role.name}, {role.held ? 'held' : 'not held'}
    </Badge>
  )
}
