import { useEffect, type ReactNode } from 'react';
import { useThemeStore } from '@/stores/useThemeStore';

interface ThemeProviderProps {
  children: ReactNode;
}

export default function ThemeProvider({ children }: ThemeProviderProps) {
  const theme = useThemeStore((s) => s.theme);

  useEffect(() => {
    // Preset convention: :root is light, `.dark` class activates dark palette (matches
    // Tailwind's darkMode: 'class' and the @custom-variant in globals.css).
    document.documentElement.classList.toggle('dark', theme === 'dark');
  }, [theme]);

  return <>{children}</>;
}
