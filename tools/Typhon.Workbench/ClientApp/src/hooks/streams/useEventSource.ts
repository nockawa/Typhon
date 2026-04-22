import { useEffect, useRef, useState } from 'react';

type ConnectionState = 'connecting' | 'open' | 'closed';

const RECONNECT_DELAY_MS = 3000;

export function useEventSource<T>(
  url: string | null,
  onMessage: (data: T) => void,
): ConnectionState {
  const [state, setState] = useState<ConnectionState>('closed');
  const onMessageRef = useRef(onMessage);
  onMessageRef.current = onMessage;

  useEffect(() => {
    if (!url) {
      setState('closed');
      return;
    }

    let es: EventSource;
    let reconnectTimer: ReturnType<typeof setTimeout>;
    let cancelled = false;

    const connect = () => {
      setState('connecting');
      es = new EventSource(url);

      es.onopen = () => {
        if (!cancelled) setState('open');
      };

      es.onmessage = (event) => {
        if (cancelled) return;
        try {
          onMessageRef.current(JSON.parse(event.data) as T);
        } catch {
          // ignore parse errors
        }
      };

      es.onerror = () => {
        es.close();
        if (!cancelled) {
          setState('closed');
          reconnectTimer = setTimeout(connect, RECONNECT_DELAY_MS);
        }
      };
    };

    connect();

    return () => {
      cancelled = true;
      clearTimeout(reconnectTimer);
      es?.close();
      setState('closed');
    };
  }, [url]);

  return state;
}
