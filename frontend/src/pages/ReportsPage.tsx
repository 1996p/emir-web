import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, useApi, type ReportParams } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { ReportColumn, ReportDefinition, ReportRow } from '@/domain/types';
import { AccordionNav, DataTable, Notice, Segmented, SelectInput, TextInput, type Column } from '@/ui';

const PERIODS = [{ value: 'Этот месяц', label: 'Этот месяц' }, { value: 'Прошлый', label: 'Прошлый' }, { value: 'Квартал', label: 'Квартал' }, { value: 'Свои даты', label: 'Свои даты' }] as const;
type Period = (typeof PERIODS)[number]['value'];

function formatCell(value: number | null | undefined, column: ReportColumn): string {
  if (value === null || value === undefined) return '—';
  if (column.type === 'money') return (value < 0 ? '−' : '') + formatMoney(value);
  if (column.type === 'percent') return (value / 10).toLocaleString('ru-RU', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  if (column.type === 'volume') return (value / 1000).toLocaleString('ru-RU', { minimumFractionDigits: 3, maximumFractionDigits: 3 });
  return value.toLocaleString('ru-RU');
}

export function ReportsPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const { data: catalog } = useApi(() => api.reports.catalog(), []);
  const [search, setSearch] = useState('');
  const [reportId, setReportId] = useState('debtors');
  const [openGroup, setOpenGroup] = useState<string | null>('Задолженность');
  const [period, setPeriod] = useState<Period>('Этот месяц');
  const [cut, setCut] = useState('Абоненты');
  const [who, setWho] = useState('Все');
  const [from, setFrom] = useState(() => new Date().toLocaleDateString('sv-SE').slice(0, 8) + '01');
  const [to, setTo] = useState(() => { const today = new Date(); return new Date(today.getFullYear(), today.getMonth() + 1, 0).toLocaleDateString('sv-SE'); });

  const definition = catalog?.find((item) => item.id === reportId) ?? null;
  const activeCut = definition && definition.cuts.includes(cut) ? cut : definition?.cuts[0] ?? cut;

  useEffect(() => {
    const wanted = params.get('report');
    const found = wanted ? catalog?.find((item) => item.name === wanted) : undefined;
    if (found) { setReportId(found.id); setOpenGroup(found.group); setCut(found.cuts[0] ?? ''); }
  }, [params, catalog]);

  const query: ReportParams = { reportId, period, cut: activeCut, who, ...(period === 'Свои даты' ? { from, to } : {}) };
  const { data: result, error } = useApi(() => api.reports.run(query), [reportId, period, activeCut, who, from, to]);

  const groups = useMemo(() => {
    const normalized = search.trim().toLowerCase();
    const items = (catalog ?? []).filter((item) => !normalized || item.name.toLowerCase().includes(normalized));
    const names = [...new Set(items.map((item) => item.group))];
    return names.map((name) => ({ name, items: items.filter((item) => item.group === name) }));
  }, [catalog, search]);

  const columns: Column<ReportRow>[] = result
    ? [
        { key: 'name', header: activeCut, render: (row) => <span style={{ fontWeight: 500 }}>{row.name}</span>, footer: 'Итого' },
        ...result.columns.map((column, index) => ({
          key: column.title, header: column.title, align: 'right' as const, mono: true,
          render: (row: ReportRow) => formatCell(row.values[index], column),
          footer: formatCell(result.totals[index], column),
        })),
      ]
    : [];

  const selectReport = (item: ReportDefinition) => { setReportId(item.id); setCut(item.cuts[0] ?? ''); };

  return (
    <div className="split">
      <aside className="pane" style={{ width: 272 }}>
        <div style={{ padding: '12px 12px 8px' }}>
          <TextInput type="search" aria-label="Найти отчёт" placeholder="Найти отчёт" value={search} onChange={(event) => setSearch(event.target.value)} />
        </div>
        <div className="scroll" style={{ padding: '0 8px 12px' }}>
          <AccordionNav groups={groups} forceOpen={Boolean(search.trim())} openGroup={openGroup} onOpenGroup={setOpenGroup} itemKey={(item) => item.id} itemLabel={(item) => item.name} isSelected={(item) => item.id === reportId} onSelect={selectReport} />
          {groups.length === 0 && <p className="muted" style={{ margin: '12px 8px' }}>Такого отчёта нет.</p>}
        </div>
      </aside>

      <main className="main">
        <div className="stack" style={{ padding: '12px 16px 10px', gap: 10, borderBottom: '1px solid var(--line)' }}>
          <div className="between" style={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <h1 className="title">{definition?.name}</h1>
            {result?.available
              ? <a className="btn btn-primary btn-md" href={api.reports.csvUrl(query)} download>Скачать CSV</a>
              : null}
          </div>
          <div className="row" style={{ gap: '8px 12px' }}>
            <Segmented label="Период" options={PERIODS} value={period} onChange={setPeriod} itemWidth={88} />
            {period === 'Свои даты' && (
              <div className="row">
                <TextInput type="date" aria-label="С даты" value={from} onChange={(event) => setFrom(event.target.value)} style={{ width: 150, minHeight: 38 }} />
                <TextInput type="date" aria-label="По дату" value={to} onChange={(event) => setTo(event.target.value)} style={{ width: 150, minHeight: 38 }} />
              </div>
            )}
            <label className="small muted row">Разрез
              <SelectInput value={activeCut} onChange={(event) => setCut(event.target.value)} style={{ width: 200, minHeight: 38 }}>
                {definition?.cuts.map((item) => <option key={item}>{item}</option>)}
              </SelectInput>
            </label>
            <label className="small muted row">Кто
              <SelectInput value={who} onChange={(event) => setWho(event.target.value)} style={{ width: 120, minHeight: 38 }}>
                <option>Все</option><option>Физлица</option><option>Юрлица</option>
              </SelectInput>
            </label>
            <span className="small muted" style={{ marginLeft: 'auto' }}>Строк: {result?.rows.length ?? 0}</span>
          </div>
          {error && <Notice tone="warn">{error}</Notice>}
        </div>

        {result && !result.available && <div className="pad"><Notice>{result.message}</Notice></div>}
        {result?.available && (
          <DataTable
            columns={columns} rows={result.rows} rowKey={(row) => row.name}
            empty={<span className="muted">За этот период данных нет.</span>}
            {...(activeCut === 'Абоненты' ? { onRowClick: (row: ReportRow) => navigate(`/abonents?q=${encodeURIComponent(row.name)}`) } : {})}
          />
        )}
      </main>
    </div>
  );
}
