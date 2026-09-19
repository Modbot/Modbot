import { Button } from '@/components/ui/button'
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
      <div className="w-full max-w-[520px]">
        <Brand />
        <div className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow={`Signed in as ${me.username}`} title="Link your VRChat account" />
          <WizardBody>
            <VRChatLinkPanel compact onLinked={onLinked} />
          </WizardBody>
          <WizardFooter>
            <Button
              type="button"
              variant="ghost"
              onClick={() => void api.logout().finally(() => window.location.assign('/'))}
              style={{ height: 'var(--control-h)' }}
            >
              Sign out
            </Button>
          </WizardFooter>
        </div>
      </div>
    </div>
  )
}
