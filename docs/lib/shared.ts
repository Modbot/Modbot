/** The site's name, as shown in the navigation bar and page titles. */
export const siteName = 'Modbot Docs';

/** What the site is, for a search result and for the link preview a chat app draws. */
export const siteDescription = 'How to host, set up and use Modbot, and its API.';

/**
 * The share image in the link preview, 1200 x 630. Relative to `metadataBase`, which makes the
 * whole address a crawler needs.
 */
export const shareImage = { url: '/og.png', width: 1200, height: 630 };

/**
 * Where the site is published. Used for page metadata only; every link inside the site is
 * relative, so a copy served from another address still works.
 */
export const siteUrl = 'https://docs.modbot.co';

/** Modbot's own public site. */
export const landingUrl = 'https://modbot.co';
