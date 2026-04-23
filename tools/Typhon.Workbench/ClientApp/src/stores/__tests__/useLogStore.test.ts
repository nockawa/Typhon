import { describe, expect, it, beforeEach } from 'vitest';
import { useLogStore, logInfo, logWarn, logError } from '../useLogStore';

describe('useLogStore', () => {
  beforeEach(() => {
    useLogStore.getState().clear();
  });

  it('starts empty', () => {
    expect(useLogStore.getState().entries).toEqual([]);
  });

  it('append adds entries in order with monotonic ids', () => {
    logInfo('a');
    logInfo('b');
    logInfo('c');
    const entries = useLogStore.getState().entries;
    expect(entries.map((e) => e.message)).toEqual(['a', 'b', 'c']);
    const ids = entries.map((e) => e.id);
    expect(ids).toEqual([...ids].sort((x, y) => x - y));
  });

  it('bounds the ring at 500 entries (drops oldest)', () => {
    for (let i = 0; i < 600; i++) {
      logInfo(`msg-${i}`);
    }
    const entries = useLogStore.getState().entries;
    expect(entries).toHaveLength(500);
    expect(entries[0].message).toBe('msg-100');
    expect(entries[499].message).toBe('msg-599');
  });

  it('preserves level and source', () => {
    logInfo('i');
    logWarn('w');
    logError('e');
    const [a, b, c] = useLogStore.getState().entries;
    expect(a.level).toBe('info');
    expect(b.level).toBe('warn');
    expect(c.level).toBe('error');
    expect(a.source).toBe('workbench-ui');
  });

  it('stores structured details alongside the message', () => {
    logInfo('open', { file: 'x.bin', dlls: ['a', 'b'] });
    const entry = useLogStore.getState().entries[0];
    expect(entry.details).toEqual({ file: 'x.bin', dlls: ['a', 'b'] });
  });

  it('clear empties the store', () => {
    logInfo('a');
    logInfo('b');
    useLogStore.getState().clear();
    expect(useLogStore.getState().entries).toEqual([]);
  });
});
