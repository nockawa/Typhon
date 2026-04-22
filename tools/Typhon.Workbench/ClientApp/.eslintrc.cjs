// ESLint v8 legacy config (`.eslintrc.cjs`) per the Phase 0 decision. ESLint v9's flat config is the future but the ecosystem
// of React/TypeScript plugins hasn't fully migrated yet; pinning to v8.57.x keeps this config readable and unblocks vite-plugin-checker
// without requiring `ESLINT_USE_FLAT_CONFIG=false` env hacks. Flat-config migration is a Phase 4+ concern.
module.exports = {
  root: true,
  env: {
    browser: true,
    es2022: true,
    node: true,
  },
  parser: '@typescript-eslint/parser',
  parserOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
    ecmaFeatures: { jsx: true },
  },
  settings: { react: { version: '19.0' } },
  plugins: ['@typescript-eslint', 'react-hooks', 'react-refresh'],
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:react-hooks/recommended',
  ],
  rules: {
    'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
  },
  ignorePatterns: ['dist', 'api', 'node_modules', '.eslintrc.cjs'],
};
