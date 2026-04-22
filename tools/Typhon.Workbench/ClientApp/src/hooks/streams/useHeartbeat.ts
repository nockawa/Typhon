import { useCallback, useRef, useState } from 'react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useEventSource } from './useEventSource';

const STALE_THRESHOLD_MS = 20_000;

export interface HeartbeatPayload {
  timestamp: string;
  seq: number;
  revision: number;
  memoryMb: number;
  // Phase 5 extensions — null until Workbench hosts a TyphonRuntime.
  tickRate: number | null;
  activeTransactionCount: number | null;
  lastTickDurationMs: number | null;
}

export interface HeartbeatState {
  status: 'green' | 'grey';
  payload: HeartbeatPayload | null;
}

export function useHeartbeat(): HeartbeatState {
  const sessionId = useSessionStore((s) => s.sessionId);
  const [status, setStatus] = useState<'green' | 'grey'>('grey');
  const [payload, setPayload] = useState<HeartbeatPayload | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const url = sessionId ? `/api/sessions/${sessionId}/heartbeat` : null;

  const onMessage = useCallback((data: HeartbeatPayload) => {
    setPayload(data);
    setStatus('green');
    clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => setStatus('grey'), STALE_THRESHOLD_MS);
  }, []);

  useEventSource<HeartbeatPayload>(url, onMessage);

  return { status, payload };
}
