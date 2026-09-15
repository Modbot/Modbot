import { StrictMode } from 'react'
import { renderToString } from 'react-dom/server'
import App from './App'
import { NotFound, Privacy } from './StaticPages'

// Used only at build time, by scripts/prerender.ts.

export const renderLanding = (privacy: boolean) =>
  renderToString(
    <StrictMode>
      <App privacy={privacy} />
    </StrictMode>,
  )

export const renderNotFound = () => renderToString(<NotFound />)

export const renderPrivacy = (html: string) => renderToString(<Privacy html={html} />)
