import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from './App';
// Load dockview's own stylesheet BEFORE globals.css so our theme-variable overrides in
// globals.css come later in the cascade and win on equal-specificity selectors like
// `.dockview-theme-dark`. Without this, the theme appears on the shell but dockview panels
// keep their vendor defaults.
import 'dockview-react/dist/styles/dockview.css';
import './globals.css';

// TanStack Query client is hoisted out of React so hot-module-reloads during dev don't reset the cache and drop every in-flight
// request. Defaults are conservative: no automatic refetch on window focus (the Workbench's data is engine-driven, not time-driven
// — a focus event doesn't imply stale data) and a single retry (the server and client are on the same machine; repeat failures
// almost always mean the engine went down, not a transient network glitch).
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
      staleTime: 30_000,
    },
  },
});

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element #root is missing from index.html — cannot mount Workbench.');
}

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
);
