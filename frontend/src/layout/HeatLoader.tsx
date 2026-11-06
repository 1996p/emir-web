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
      <svg className="heat-loader-icon" viewBox="0 0 120 96" aria-hidden="true">
        <defs>
          <linearGradient id="heat-cold" x1="0" x2="1">
            <stop offset="0" stopColor="#b9d3ec" /><stop offset=".5" stopColor="#e8f1fa" /><stop offset="1" stopColor="#b9d3ec" />
          </linearGradient>
        </defs>
        {[34, 52, 70, 88].map((x, index) => (
          <path key={x} className={`heat-wave heat-wave-${index}`} d={`M${x} 24 q-4 -4 0 -8 q4 -4 0 -8`} />
        ))}
        {/* подводка с краном */}
        <rect className="heat-metal" x="4" y="36" width="16" height="5" rx="2.5" />
        <rect className="heat-metal-dark" x="9" y="30" width="6" height="6" rx="1.5" />
        <rect className="heat-metal" x="6" y="27" width="12" height="4" rx="2" />
        <rect className="heat-metal" x="4" y="75" width="16" height="5" rx="2.5" />
        {/* коллекторы и заглушки */}
        <rect className="heat-metal" x="18" y="35" width="90" height="7" rx="3.5" />
        <rect className="heat-metal" x="18" y="74" width="90" height="7" rx="3.5" />
        <rect className="heat-metal-dark" x="106" y="34" width="5" height="9" rx="1.5" />
        <rect className="heat-metal-dark" x="106" y="73" width="5" height="9" rx="1.5" />
        {/* секции с рёбрами */}
        {[0, 1, 2, 3, 4, 5, 6].map((index) => (
          <g key={index} className={`heat-section heat-section-${index}`}>
            <rect x={21 + index * 12} y="28" width="10" height="60" rx="5" />
            <line x1={26 + index * 12} y1="34" x2={26 + index * 12} y2="82" />
          </g>
        ))}
        {/* ножки */}
        <rect className="heat-metal-dark" x="28" y="87" width="4" height="7" rx="1" />
        <rect className="heat-metal-dark" x="92" y="87" width="4" height="7" rx="1" />
      </svg>
      </div>
      <span className="sr-only">Загрузка</span>
    </div>
  );
}
