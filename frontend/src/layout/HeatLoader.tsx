import { useEffect, useState, useSyncExternalStore } from 'react';
import { getPendingRequests, subscribeLoading } from '@/api';

/** Батарея отопления, которая прогревается, пока экран ждёт первых данных. Появляется не сразу, чтобы не мигать на быстрых ответах. */
export function HeatLoader() {
  const pending = useSyncExternalStore(subscribeLoading, getPendingRequests);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (pending === 0) {
      setVisible(false);
      return;
    }
    const timer = window.setTimeout(() => setVisible(true), 250);
    return () => window.clearTimeout(timer);
  }, [pending]);

  if (!visible) return null;
  return (
    <div className="heat-loader" role="status" aria-live="polite">
      <div className="heat-loader-card">
      <svg className="heat-loader-icon" viewBox="0 0 48 40" aria-hidden="true">
        <path className="heat-wave" d="M14 12 q-3 -3 0 -6 q3 -3 0 -6" />
        <path className="heat-wave heat-wave-2" d="M24 12 q-3 -3 0 -6 q3 -3 0 -6" />
        <path className="heat-wave heat-wave-3" d="M34 12 q-3 -3 0 -6 q3 -3 0 -6" />
        {[0, 1, 2, 3, 4].map((index) => (
          <rect key={index} className={`heat-section heat-section-${index}`} x={7 + index * 7.5} y="14" width="6" height="22" rx="3" />
        ))}
      </svg>
      </div>
      <span className="sr-only">Загрузка</span>
    </div>
  );
}
