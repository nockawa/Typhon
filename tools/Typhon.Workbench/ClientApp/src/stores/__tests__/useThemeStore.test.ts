import { beforeEach, describe, expect, it } from 'vitest';
import { useThemeStore } from '../useThemeStore';

beforeEach(() => {
  useThemeStore.setState({ theme: 'dark' });
});

describe('useThemeStore', () => {
  it('defaults to dark', () => {
    expect(useThemeStore.getState().theme).toBe('dark');
  });

  it('toggle switches dark → light', () => {
    useThemeStore.getState().toggle();
    expect(useThemeStore.getState().theme).toBe('light');
  });

  it('toggle switches light → dark', () => {
    useThemeStore.setState({ theme: 'light' });
    useThemeStore.getState().toggle();
    expect(useThemeStore.getState().theme).toBe('dark');
  });

  it('setTheme sets explicit value', () => {
    useThemeStore.getState().setTheme('light');
    expect(useThemeStore.getState().theme).toBe('light');
    useThemeStore.getState().setTheme('dark');
    expect(useThemeStore.getState().theme).toBe('dark');
  });
});
