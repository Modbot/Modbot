import { loader } from 'fumadocs-core/source';
import { defineDocs } from 'fumadocs-mdx/macro';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';
import type { DistributiveOmit, OperationOutput, PagesBuilder, WebhookOutput } from 'fumadocs-openapi';
import { openapi } from './openapi';

/** Lower-case words joined by dashes, for URLs: "API keys" and "ApiKeys" both become "api-keys". */
function toSlug(name: string): string {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/[^A-Za-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .toLowerCase();
}

const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
  },
  meta: {
    schema: metaSchema,
  },
});

/** One page per API endpoint, in a folder per OpenAPI tag, under api/reference. */
const reference = await openapi.staticSource({
  baseDir: 'api/reference',
  groupBy: 'tag',
  meta: true,
  slugify: toSlug,
  // Named after the operation id: CreateApiKey becomes create-api-key.
  name(this: PagesBuilder, output: DistributiveOmit<OperationOutput | WebhookOutput, 'path'>) {
    if (output.type === 'operation') {
      const operation = this.document.paths?.[output.item.path]?.[output.item.method];
      return toSlug(operation?.operationId ?? `${output.item.method} ${output.item.path}`);
    }
    return toSlug(output.item.name);
  },
});

// The list of sections comes from content/docs/api/reference/meta.json, in the order the OpenAPI
// document lists its tags. The generated list follows the order each tag's first endpoint
// appears in, which reads as random.
reference.files = reference.files.filter(
  (file) => !(file.type === 'meta' && file.path.replaceAll('\\', '/') === 'api/reference/meta.json'),
);

/** Every page on the site: the written pages in content/docs, and the API reference. */
export const source = loader(
  {
    docs: docs.toFumadocsSource(),
    openapi: reference,
  },
  {
    baseUrl: '/',
    plugins: [openapi.loaderPlugin()],
  },
);
