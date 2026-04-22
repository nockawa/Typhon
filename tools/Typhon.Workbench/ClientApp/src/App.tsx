import Shell from '@/shell/Shell';
import ThemeProvider from '@/shell/ThemeProvider';

export default function App() {
  return (
    <ThemeProvider>
      <Shell />
    </ThemeProvider>
  );
}
