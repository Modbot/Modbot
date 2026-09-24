import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { VRChatLinkPanel } from '@/components/VRChatLinkPanel'
import { api, type CurrentUser } from '@/lib/api'
import { Brand, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/**
 * Where a signed-in person lands until they have linked their VRChat account (accounts and
 * access design §4.3). Outside the app shell, because there is nothing in it they can use yet.
 */
export function LinkVRChat({ me, onLinked }: { me: CurrentUser; onLinked: () => void }) {
  return (
    <div className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="w-full min-w-0 max-w-[520px]">
        <Brand />
        <Card>
          <WizardHeader eyebrow={`Signed in as ${me.username}`} title="Link your VRChat account" />
          <WizardBody>
            <VRChatLinkPanel compact onLinked={onLinked} />
          </WizardBody>
          <WizardFooter>
            <Button
              type="button"
              variant="ghost"
              onClick={() => void api.logout().finally(() => window.location.assign('/'))}
            >
              Sign out
            </Button>
          </WizardFooter>
        </Card>
      </div>
    </div>
  )
}
