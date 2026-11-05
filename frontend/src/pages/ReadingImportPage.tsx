import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { ReadingImportLine, ReadingImportResult } from '@/domain/types';
import { Button, DataTable, Field, Notice, SelectInput, TextInput, type Column } from '@/ui';

const STATES: Record<string, string> = {
  ok: 'Будет записано', high: 'Большой расход', same: 'Уже внесено', 'not-found': 'Счёт не найден', 'no-meter': 'У абонента нет счётчика',
  'many-meters': 'Несколько счётчиков, укажите номер', 'unknown-meter': 'Счётчик не найден', lower: 'Меньше прошлого', 'bad-value': 'Неверное значение',
  'bad-date': 'Неверная дата', 'other-month': 'Другой месяц', duplicate: 'Повтор в файле',
};
const stateClass = (state: string) => (state === 'ok' ? 'ok' : state === 'same' ? 'muted' : 'warn strong');

const number = (value: number | null) => (value === null ? '—' : value.toLocaleString('ru-RU', { maximumFractionDigits: 4 }));

const COLUMNS: Column<ReadingImportLine>[] = [
  { key: 'line', header: 'Строка', align: 'right', mono: true, render: (row) => row.line },
  { key: 'account', header: 'ЛС', mono: true, render: (row) => row.account },
  { key: 'meter', header: 'Счётчик', mono: true, render: (row) => row.meter || '—' },
  { key: 'previous', header: 'На начало месяца', align: 'right', mono: true, render: (row) => number(row.previous) },
  { key: 'value', header: 'Показание', align: 'right', mono: true, render: (row) => row.value || '—' },
  { key: 'usage', header: 'Расход', align: 'right', mono: true, render: (row) => number(row.usage) },
  { key: 'charge', header: 'Добавится к долгу, ₽', align: 'right', mono: true, render: (row) => (row.state === 'ok' || row.state === 'high' ? formatMoney(row.charge) : '—') },
  { key: 'state', header: 'Результат', render: (row) => <span className={stateClass(row.state)}>{STATES[row.state] ?? row.state}</span> },
];

const COLUMN_NUMBER = /^\d+$/;

export function ReadingImportPage() {
  const [file, setFile] = useState<File | null>(null);
  const [separator, setSeparator] = useState('semicolon');
  const [account, setAccount] = useState('1');
  const [meter, setMeter] = useState('2');
  const [value, setValue] = useState('3');
  const [date, setDate] = useState('0');
  const [includeHigh, setIncludeHigh] = useState(false);
  const [result, setResult] = useState<ReadingImportResult | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<'all' | 'problems'>('all');

  const columns = { separator, accountColumn: account, meterColumn: meter, valueColumn: value, dateColumn: date };
  const validColumns = [account, meter, value, date].every((item) => COLUMN_NUMBER.test(item));
  const reset = () => { setResult(null); setMessage(null); setError(null); };
  const toSave = result ? result.applicable + (includeHigh ? result.high : 0) : 0;
  const charge = result?.lines.filter((line) => line.state === 'ok' || (includeHigh && line.state === 'high')).reduce((sum, line) => sum + line.charge, 0) ?? 0;
  const rows = (result?.lines ?? []).filter((line) => filter === 'all' || (line.state !== 'ok' && line.state !== 'same'));

  const check = async () => {
    if (!file) return;
    reset();
    try {
      setResult(await api.readings.importFile(file, columns, {}));
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const apply = async () => {
    if (!file) return;
    setError(null);
    try {
      const saved = await api.readings.importFile(file, columns, { apply: true, includeHigh, expected: toSave });
      setMessage(`Записано показаний: ${saved.saved}, к долгу добавлено ${formatMoney(charge)} ₽.`);
      setResult(null);
      setFile(null);
    } catch (reason) {
      setError(errorText(reason));
    }
  };

  const numberField = (label: string, current: string, set: (next: string) => void, hint?: string) => (
    <Field label={label} {...(hint ? { hint } : {})}>{(id) => <TextInput id={id} mono inputMode="numeric" value={current} onChange={(event) => { set(event.target.value); reset(); }} />}</Field>
  );

  return (
    <div className="split">
      <main className="main">
        <div className="bar" style={{ justifyContent: 'space-between' }}>
          <h1 className="title">Показания из файла{result ? `: ${result.title.toLowerCase()}` : ''}</h1>
          <div className="row">
            {result && <SelectInput aria-label="Какие строки показать" value={filter} onChange={(event) => setFilter(event.target.value as 'all' | 'problems')} style={{ width: 170 }}><option value="all">Все строки</option><option value="problems">Только проблемы</option></SelectInput>}
            <Link to="/readings" className="btn" style={{ width: 148 }}>К показаниям</Link>
          </div>
        </div>
        {message && <div style={{ padding: '8px 16px' }}><Notice tone="ok">{message}</Notice></div>}
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        <DataTable columns={COLUMNS} rows={rows} rowKey={(row) => String(row.line)} minWidth={760} empty={<span className="muted">{result ? 'Проблемных строк нет.' : 'Выберите файл справа и нажмите «Проверить файл».'}</span>} />
      </main>
      <aside className="pane pane-right" aria-label="Настройка загрузки" style={{ width: 320 }}>
        <div className="scroll stack pad">
          <Field label="Файл CSV или TXT">{(id) => <input id={id} type="file" accept=".csv,.txt" onChange={(event) => { setFile(event.target.files?.[0] ?? null); reset(); }} />}</Field>
          <Field label="Разделитель">{(id) => <SelectInput id={id} value={separator} onChange={(event) => { setSeparator(event.target.value); reset(); }}><option value="semicolon">Точка с запятой</option><option value="comma">Запятая</option><option value="tab">Табуляция</option></SelectInput>}</Field>
          <h2 className="section-title" style={{ marginTop: 4 }}>Номера колонок</h2>
          {numberField('Лицевой счёт', account, setAccount)}
          {numberField('Счётчик', meter, setMeter, '0: колонки нет. Подойдёт, если у абонента один счётчик.')}
          {numberField('Показание', value, setValue)}
          {numberField('Дата', date, setDate, '0: колонки нет. Если есть, строки другого месяца не запишутся.')}
          <Button size="full" disabled={!file || !validColumns} onClick={() => void check()}>Проверить файл</Button>
        </div>
        {result && (
          <div className="stack pad" style={{ background: 'var(--surface)', borderTop: '1px solid var(--line)', gap: 8 }}>
            {result.high > 0 && <label className="small row" style={{ gap: 6 }}><input type="checkbox" checked={includeHigh} onChange={(event) => setIncludeHigh(event.target.checked)} />Записать и большой расход: {result.high}</label>}
            <div className="between strong"><span>К записи: {toSave}</span><span className="mono">+{formatMoney(charge)} ₽</span></div>
            <p className="small muted" style={{ lineHeight: 1.4 }}>Если показание за месяц уже вносили, к долгу добавится только разница.</p>
            <Button variant="primary" size="lg" disabled={toSave === 0} onClick={() => void apply()}>Записать показания</Button>
          </div>
        )}
      </aside>
    </div>
  );
}
