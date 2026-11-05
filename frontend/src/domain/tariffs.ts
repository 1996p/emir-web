const MONTHS = ['январь', 'февраль', 'март', 'апрель', 'май', 'июнь', 'июль', 'август', 'сентябрь', 'октябрь', 'ноябрь', 'декабрь'];

/** Первая запись истории: такие цены действовали до начала учёта. */
export const HISTORY_START = '2000-01';

export function formatPeriod(period: string): string {
  if (period === HISTORY_START) return 'С начала учёта';
  const [year, month] = period.split('-');
  const name = MONTHS[Number(month) - 1];
  return name ? `${name} ${year}` : period;
}

export function nextPeriod(period: string): string {
  const [year, month] = period.split('-').map(Number);
  if (!year || !month) return '';
  return month === 12 ? `${year + 1}-01` : `${year}-${String(month + 1).padStart(2, '0')}`;
}

export function formatPrice(price: number): string {
  return price.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
}

/** Принимает запятую и точку. Возвращает null, если это не цена больше нуля с четырьмя знаками максимум. */
export function parsePrice(text: string): number | null {
  const normalized = text.trim().replace(/\s/g, '').replace(',', '.');
  if (!/^\d+(\.\d{1,4})?$/.test(normalized)) return null;
  const value = Number(normalized);
  return value > 0 ? value : null;
}
