import type { ConnectionDiagnosis } from '@/lib/api'
import { Note } from './WizardChrome'

/**
 * One connection attempt, rendered.
 *
 * Every sentence here comes from the server. That is deliberate: spec 7.1.1's whole point is that
 * a Cloudflare block, a DNS failure, a timeout and a wrong password need four different answers,
 * and keeping the wording in one place is what stops the browser and the API drifting into
 * disagreeing about which one the operator is looking at.
 */
export function DiagnosisNote({ diagnosis }: { diagnosis: ConnectionDiagnosis }) {
  const ok = diagnosis.outcome === 'Ok'

  return (
    <Note tone={ok ? 'ok' : diagnosis.proxyWouldHelp ? 'warn' : 'danger'} title={diagnosis.headline}>
      {diagnosis.detail}
      {ok && (
        <>
          {' '}
          <span className="font-mono">{diagnosis.elapsedMs} ms</span>.
        </>
      )}
      {diagnosis.nextStep && (
        <>
          <br />
          <span className="mt-1 inline-block">{diagnosis.nextStep}</span>
        </>
      )}
      {diagnosis.wafCode ? (
        <>
          <br />
          <span className="font-mono text-muted-foreground/70">
            Cloudflare error {diagnosis.wafCode}
          </span>
        </>
      ) : null}
    </Note>
  )
}
