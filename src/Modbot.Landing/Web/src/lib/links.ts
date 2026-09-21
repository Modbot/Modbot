/**
 * Where the pages send people.
 *
 * `/discord` and `/github` are this site's own addresses. The server sends them on to whatever
 * `MODBOT_DISCORD_URL` and `MODBOT_GITHUB_URL` hold, so the pages are built once and the addresses
 * can change without building them again. `/discord` answers with the not-found page while no
 * address is set.
 */
export const OPEN_MY_SERVER = 'https://my.modbot.co/go'
export const MY_MODBOT = 'https://my.modbot.co'
export const SITE = 'https://modbot.co'
export const DISCORD = '/discord'
export const GITHUB = '/github'

/** The founder's own site, behind his name in the footer. */
export const FOUNDER = 'https://bin.moe'

/**
 * The documentation site. Its address is not final; this is the one place to change it. "Host your
 * own" goes to its self-hosting guide.
 */
export const DOCS = 'https://docs.modbot.co'
export const SELF_HOSTING_GUIDE = `${DOCS}/self-hosting/`

/**
 * The install script this site serves, and the page about it. The address in the command on the
 * self-hosting page is the link, so reading it before running it is one click and needs no
 * sentence telling people they can.
 */
export const INSTALL_SCRIPT = `${SITE}/get.sh`
export const INSTALL_GUIDE = `${DOCS}/self-hosting/install-script`

/** Railway's one-click deploy for Modbot, behind their own button. */
export const DEPLOY_ON_RAILWAY = 'https://railway.com/deploy/modbot'

/** The pages of this site, in the order the header lists them. */
export const PAGES = {
  home: '/',
  features: '/features',
  instances: '/instances',
  selfHost: '/self-host',
  about: '/about',
  license: '/license',
  privacy: '/privacy',
} as const

export type PageName = keyof typeof PAGES
