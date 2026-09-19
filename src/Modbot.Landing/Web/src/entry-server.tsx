import { StrictMode, type ReactNode } from 'react'
import { renderToString } from 'react-dom/server'
import { About } from './pages/About'
import { Features } from './pages/Features'
import { Home } from './pages/Home'
import { Instances } from './pages/Instances'
import { License } from './pages/License'
import { NoDiscord } from './pages/NoDiscord'
import { NotFound } from './pages/NotFound'
import { Privacy } from './pages/Privacy'
import { SelfHost } from './pages/SelfHost'

// Used only at build time, by scripts/prerender.ts. Each page takes whether the privacy policy was
// built, because that decides whether the footer links to it.

const render = (page: ReactNode) => renderToString(<StrictMode>{page}</StrictMode>)

export const renderHome = (privacy: boolean) => render(<Home privacy={privacy} />)

export const renderFeatures = (privacy: boolean) => render(<Features privacy={privacy} />)

export const renderSelfHost = (privacy: boolean) => render(<SelfHost privacy={privacy} />)

export const renderAbout = (privacy: boolean, contributors: { name: string; url: string }[]) =>
  render(<About privacy={privacy} contributors={contributors} />)

export const renderLicense = (privacy: boolean, text: string) => render(<License privacy={privacy} text={text} />)

// The instances themselves are fetched in the browser, so what is rendered here is the page around
// an empty list: the words, the header and the footer are in the file a search engine fetches.
export const renderInstances = (privacy: boolean) => render(<Instances privacy={privacy} />)

export const renderPrivacy = (html: string) => render(<Privacy html={html} />)

export const renderNoDiscord = (privacy: boolean) => render(<NoDiscord privacy={privacy} />)

export const renderNotFound = () => render(<NotFound />)
