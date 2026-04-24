import { create } from 'zustand';
import type { ProfilerMetadataDto } from '@/api/generated/model';
import type { TraceEvent } from '@/libs/profiler/model/types';

/**
 * Mirror of the server's BuildProgressDto. Orval regen may emit a generated type for this; wire through when available.
 */
export interface BuildProgressPayload {
  phase: 'building' | 'done' | 'error';
  bytesRead?: number;
  totalBytes?: number;
  tickCount?: number;
  eventCount?: number;
  message?: string;
}

/**
 * One tick's worth of decoded records — mirrors the server's `LiveTickBatch` DTO. The server broadcasts these
 * over the `/profiler/stream` SSE endpoint wrapped in a {@link LiveStreamPayload}.
 */
export interface LiveTickBatch {
  tickNumber: number;
  events: TraceEvent[];
}

/**
 * Discriminated union matching the server's `LiveStreamEventDto`. Every SSE frame is a default `message` event
 * with one of these payload shapes; clients switch on `kind`.
 */
export type LiveStreamPayload =
  | { kind: 'metadata'; metadata: ProfilerMetadataDto }
  | { kind: 'tick'; tick: LiveTickBatch }
  | { kind: 'heartbeat'; status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected' };

/** Live connection status — mirrors the server's `AttachSessionRuntime.ConnectionStatus`. */
export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** Ring-buffer cap for live tick retention (Phase 1b — rendering in Phase 2 consumes this buffer). */
export const LIVE_TICK_BUFFER_CAP = 1000;

interface ProfilerSessionStoreState {
  /** Non-null once metadata lands from `/profiler/metadata` (Trace: after build; Attach: after Init frame). */
  metadata: ProfilerMetadataDto | null;
  /** Latest build-progress frame from the Trace SSE stream; null until first frame arrives. */
  buildProgress: BuildProgressPayload | null;
  /** Non-null if the Trace build failed. Shown by the panel's error branch. */
  buildError: string | null;

  // ── Attach-mode live state ──────────────────────────────────────────────────────────
  /** True when the active session is `kind === 'attach'`. Panels flip UI affordances based on this. */
  isLive: boolean;
  /** Live connection status; null when no live session is active. */
  connectionStatus: ConnectionStatus | null;
  /** Total ticks received since the session started. Survives reconnects — server-side counter resets, client accumulates. */
  liveTickCount: number;
  /** User's follow/pause state for the live timeline. Default true on new live sessions. */
  liveFollowActive: boolean;
  /** Ring buffer of the most recent tick batches (cap {@link LIVE_TICK_BUFFER_CAP}). Phase 2 renders from this. */
  recentTicks: LiveTickBatch[];

  setMetadata: (metadata: ProfilerMetadataDto) => void;
  setBuildProgress: (progress: BuildProgressPayload) => void;
  setBuildError: (message: string) => void;

  setIsLive: (isLive: boolean) => void;
  setConnectionStatus: (status: ConnectionStatus) => void;
  appendLiveTicks: (batches: LiveTickBatch[]) => void;
  setLiveFollowActive: (active: boolean) => void;

  reset: () => void;
}

export const useProfilerSessionStore = create<ProfilerSessionStoreState>()((set) => ({
  metadata: null,
  buildProgress: null,
  buildError: null,

  isLive: false,
  connectionStatus: null,
  liveTickCount: 0,
  liveFollowActive: true,
  recentTicks: [],

  setMetadata: (metadata) => set({ metadata, buildError: null }),
  setBuildProgress: (progress) => set({ buildProgress: progress }),
  setBuildError: (message) => set({ buildError: message }),

  setIsLive: (isLive) => set({ isLive }),
  setConnectionStatus: (status) => set({ connectionStatus: status }),
  appendLiveTicks: (batches) =>
    set((s) => {
      if (batches.length === 0) return s;
      // Preserve the newest N ticks when exceeding cap — drop-oldest semantics matches the server's bounded channel.
      const merged = s.recentTicks.concat(batches);
      const trimmed = merged.length > LIVE_TICK_BUFFER_CAP ? merged.slice(merged.length - LIVE_TICK_BUFFER_CAP) : merged;
      return {
        recentTicks: trimmed,
        liveTickCount: s.liveTickCount + batches.length,
      };
    }),
  setLiveFollowActive: (active) => set({ liveFollowActive: active }),

  reset: () =>
    set({
      metadata: null,
      buildProgress: null,
      buildError: null,
      isLive: false,
      connectionStatus: null,
      liveTickCount: 0,
      liveFollowActive: true,
      recentTicks: [],
    }),
}));
