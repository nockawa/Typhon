import { defineConfig } from 'orval';

export default defineConfig({
  workbench: {
    input: './schema/openapi.json',
    output: {
      mode: 'tags-split',
      target: './src/api/generated/endpoints.ts',
      schemas: './src/api/generated/model',
      client: 'react-query',
      override: {
        mutator: {
          path: './src/api/client.ts',
          name: 'customFetch',
        },
      },
      clean: true,
    },
  },
});
