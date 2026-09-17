import { StrictMode } from 'react'
import { renderToString } from 'react-dom/server'
import App from './App'
import InstancesPage from './InstancesPage'
import { NotFound, Privacy } from './StaticPages'

// Used only at build time, by scripts/prerender.ts.

export const renderLanding = (privacy: boolean) =>
  renderToString(
    <StrictMode>
      <App privacy={privacy} />
    </StrictMode>,
  )

// The instances themselves are fetched in the browser, so what is rendered here is the page around an
// empty list: the words, the header and the footer are in the file a search engine fetches.
export const renderInstances = (privacy: boolean) =>
  renderToString(
    <StrictMode>
      <InstancesPage privacy={privacy} />
    </StrictMode>,
  )

export const renderNotFound = () => renderToString(<NotFound />)

export const renderPrivacy = (html: string) => renderToString(<Privacy html={html} />)
