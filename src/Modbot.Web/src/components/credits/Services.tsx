import { CreditCard } from '@/components/credits/CreditRow'
import credits from '@/lib/credits.json'

/** The services and data Modbot is built on, and who they belong to. */
export function Services() {
  return (
    <div className="flex flex-col gap-4">
      <CreditCard title="Services and data" groups={[{ items: credits.services }]} />
    </div>
  )
}
