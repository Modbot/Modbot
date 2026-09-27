/**
 * A head count on screen: the number, and a "?" after it when it is unsure (`headCountText`). The "?"
 * is in the number's own font, and its tooltip is one word.
 */
export function HeadCount({
  count,
  unsure,
  format = String,
}: {
  count: number
  unsure: boolean
  format?: (n: number) => string
}) {
  return (
    <>
      {format(count)}
      {unsure && <span title="unconfirmed">?</span>}
    </>
  )
}
