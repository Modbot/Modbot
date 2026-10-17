# Bluesky GIFs through Klipy (2026-10-17)

## Why the browser searches Klipy itself

The calendar form's GIF button searches Klipy straight from the moderator's browser, and the Klipy key is
handed to signed-in moderators who can edit events. The first plan had Modbot's server do the search so the
key never left the server, but Klipy's integration requirements (docs.klipy.com/integration-requirements)
rule that out:

- API requests and media loads must come from the user's app or browser, not through a partner's server,
  proxy or CDN, without Klipy's written approval.
- Klipy media must not be stored, mirrored or re-hosted. Modbot keeps only the GIF's address, and Bluesky
  shows the GIF from Klipy (`app.bsky.embed.external` with the `static.klipy.com` address, no thumbnail
  upload).
- Results are shown in Klipy's order, unfiltered by Modbot. The search box placeholder reads "Search KLIPY".

The search uses `content_filter=high`.

GIPHY was ruled out as well: on top of the same browser-only rule, it forbids keeping media addresses, which
a post scheduled days ahead has to do.

## Later: ask Klipy to let Modbot's server search

**To do:** email developers@klipy.com for written approval to search through Modbot's server. With it, the
search can move behind a Modbot endpoint that adds the key, and the key stays off moderators' browsers.
Until then the browser-direct search stays, because a server search would break Klipy's terms and they could
cut off the key.
