'use client';
import { createOpenAPIPage } from 'fumadocs-openapi/ui';

/**
 * Renders one endpoint of the API reference.
 *
 * The "try it" playground is off: every Modbot is somebody's own server at its own address, and
 * a browser on this site cannot call it (the server sends no cross-origin headers), so the
 * playground would only ever fail. The example requests are generated from the document instead.
 */
export const OpenAPIPage = createOpenAPIPage({
  playground: { enabled: false },
});
