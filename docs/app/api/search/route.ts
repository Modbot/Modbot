import { source } from '@/lib/source';
import { createFromSource } from 'fumadocs-core/search/server';

export const revalidate = false;

// Written once at build time as a static file; the search dialog downloads it and searches locally.
export const { staticGET: GET } = createFromSource(source, {
  language: 'english',
});
