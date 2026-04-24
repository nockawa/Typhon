import { beforeEach, describe, expect, it } from 'vitest';
import {
  useProfilerSessionStore,
  LIVE_TICK_BUFFER_CAP,
  type LiveTickBatch,
  type BuildProgressPayload,
} from '@/stores/useProfilerSessionStore';
import type { ProfilerMetadataDto } from '@/api/generated/model';

/**
 * Covers the session-store lifecycle that E1-1 (trace→trace cleanup) and E1-2 (kind-switch) hit
 * end-to-end. Without unit coverage here, a regression in `reset()` would only surface as a
 * mysteriously-flaky Playwright failure; these tests fail in &lt;10 ms on the precise bit that
 * regressed.
 */

function makeMetadata(overrides: Partial<ProfilerMetadataDto> = {}): ProfilerMetadataDto {
  return {
    fingerprint: 'abc',
    header: { timestampFrequency: 10_000_000 } as ProfilerMetadataDto['header'],
    systems: [],
    archetypes: [],
    componentTypes: [],
    spanNames: {},
    globalMetrics: {} as ProfilerMetadataDto['globalMetrics'],
    tickSummaries: [],
    chunkManifest: [],
    gcSuspensions: [],
    ...overrides,
  };
}

function makeBatch(tickNumber: number): LiveTickBatch {
  return { tickNumber, events: [] };
}

describe('useProfilerSessionStore — lifecycle', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('setMetadata clears a prior buildError', () => {
    useProfilerSessionStore.getState().setBuildError('oops');
    expect(useProfilerSessionStore.getState().buildError).toBe('oops');

    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    expect(useProfilerSessionStore.getState().metadata).not.toBeNull();
    expect(useProfilerSessionStore.getState().buildError).toBeNull();
  });

  it('setBuildProgress updates the current frame without touching other fields', () => {
    const p: BuildProgressPayload = { phase: 'building', bytesRead: 100, totalBytes: 1000 };
    useProfilerSessionStore.getState().setBuildProgress(p);
    expect(useProfilerSessionStore.getState().buildProgress).toBe(p);
    expect(useProfilerSessionStore.getState().metadata).toBeNull();
  });

  it('setIsLive and setConnectionStatus mirror the server runtime state', () => {
    useProfilerSessionStore.getState().setIsLive(true);
    expect(useProfilerSessionStore.getState().isLive).toBe(true);

    useProfilerSessionStore.getState().setConnectionStatus('connected');
    expect(useProfilerSessionStore.getState().connectionStatus).toBe('connected');
  });
});

describe('useProfilerSessionStore — live-tick ring buffer', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('appendLiveTicks([]) is a no-op (empty batch short-circuit)', () => {
    const before = useProfilerSessionStore.getState();
    useProfilerSessionStore.getState().appendLiveTicks([]);
    const after = useProfilerSessionStore.getState();
    expect(after.recentTicks).toBe(before.recentTicks);
    expect(after.liveTickCount).toBe(before.liveTickCount);
  });

  it('appendLiveTicks accumulates liveTickCount across calls', () => {
    useProfilerSessionStore.getState().appendLiveTicks([makeBatch(1), makeBatch(2)]);
    useProfilerSessionStore.getState().appendLiveTicks([makeBatch(3)]);
    expect(useProfilerSessionStore.getState().liveTickCount).toBe(3);
    expect(useProfilerSessionStore.getState().recentTicks.map((t) => t.tickNumber)).toEqual([1, 2, 3]);
  });

  it('recentTicks ring buffer drops oldest at the cap', () => {
    const overshoot = LIVE_TICK_BUFFER_CAP + 20;
    const batches = Array.from({ length: overshoot }, (_, i) => makeBatch(i + 1));
    useProfilerSessionStore.getState().appendLiveTicks(batches);

    const s = useProfilerSessionStore.getState();
    expect(s.recentTicks).toHaveLength(LIVE_TICK_BUFFER_CAP);
    // Oldest surviving is the (overshoot - CAP + 1)th tick, i.e. tick number 21 at cap=1000.
    expect(s.recentTicks[0].tickNumber).toBe(overshoot - LIVE_TICK_BUFFER_CAP + 1);
    expect(s.recentTicks[s.recentTicks.length - 1].tickNumber).toBe(overshoot);
    // liveTickCount tracks the TOTAL number of batches seen — not capped. Clients use this for
    // the "N ticks received" pill even after the ring drops old entries.
    expect(s.liveTickCount).toBe(overshoot);
  });
});

describe('useProfilerSessionStore — reset', () => {
  beforeEach(() => {
    useProfilerSessionStore.getState().reset();
  });

  it('reset wipes every field back to the initial state', () => {
    // Populate every field meaningfully.
    useProfilerSessionStore.getState().setMetadata(makeMetadata());
    useProfilerSessionStore.getState().setBuildProgress({ phase: 'done' });
    useProfilerSessionStore.getState().setBuildError('prior failure');
    useProfilerSessionStore.getState().setIsLive(true);
    useProfilerSessionStore.getState().setConnectionStatus('reconnecting');
    useProfilerSessionStore.getState().appendLiveTicks([makeBatch(1), makeBatch(2)]);
    useProfilerSessionStore.getState().setLiveFollowActive(false);

    useProfilerSessionStore.getState().reset();

    const s = useProfilerSessionStore.getState();
    expect(s.metadata).toBeNull();
    expect(s.buildProgress).toBeNull();
    expect(s.buildError).toBeNull();
    expect(s.isLive).toBe(false);
    expect(s.connectionStatus).toBeNull();
    expect(s.liveTickCount).toBe(0);
    expect(s.liveFollowActive).toBe(true); // default is true (not false)
    expect(s.recentTicks).toEqual([]);
  });

  it('reset does not leak subscription refs to callers (fresh identity)', () => {
    useProfilerSessionStore.getState().appendLiveTicks([makeBatch(1)]);
    const prevTicksRef = useProfilerSessionStore.getState().recentTicks;
    useProfilerSessionStore.getState().reset();
    // A subsequent append must create a fresh array — reset's new [] is the baseline.
    useProfilerSessionStore.getState().appendLiveTicks([makeBatch(5)]);
    expect(useProfilerSessionStore.getState().recentTicks).not.toBe(prevTicksRef);
  });
});
