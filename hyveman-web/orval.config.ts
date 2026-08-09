import { defineConfig } from 'orval';

// Orval generates the TypeScript API client from the pinned OpenAPI document
// (openapi/openapi.json). Generated output lives in src/api/generated and is
// replaced wholesale during regeneration — never edit it by hand.
export default defineConfig({
  hyveman: {
    input: './openapi/openapi.json',
    output: {
      target: './src/api/generated/endpoints.ts',
      mode: 'single',
      client: 'react-query',
      httpClient: 'fetch',
      clean: true,
      prettier: true,
      override: {
        mutator: {
          path: './src/api/client.ts',
          name: 'httpFetch',
        },
        query: {
          useQuery: true,
          useMutation: true,
          useInfinite: false,
          signal: true,
        },
      },
    },
  },
});
