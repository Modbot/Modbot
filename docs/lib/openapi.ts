import { createOpenAPI } from 'fumadocs-openapi/server';

/**
 * The Modbot API's OpenAPI document. Building src/Modbot.Host writes it; CI fails when the
 * committed copy is out of date, so the reference here always matches the server's endpoints.
 */
export const openapi = createOpenAPI({
  input: ['./openapi/modbot.json'],
});
