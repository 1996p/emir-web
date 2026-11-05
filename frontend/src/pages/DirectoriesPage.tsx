import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { useRole } from '@/layout/role';
import { BeneficiariesDirectory, CategoriesDirectory } from './BenefitsDirectory';
import { TariffsDirectory } from './TariffsDirectory';
import { AccordionNav, Button, DataTable, Notice, TextInput, type Column } from '@/ui';

interface Row { key: string; cells: string[] }

export function DirectoriesPage() {
  const role = useRole();
  const [params] = useSearchParams();
  const { data: groups } = useApi(() => api.directories.groups(), []);
  const [search, setSearch] = useState('');
  const [name, setName] = useState('Люди');
  const [openGroup, setOpenGroup] = useState<string | null>('Люди и организации');
  const [rowFilter, setRowFilter] = useState('');
  const [rowIndex, setRowIndex] = useState(0);
  const [passport, setPassport] = useState<string | null>(null);
  const { data: table, error } = useApi(() => api.directories.table(name), [name]);

  useEffect(() => {
    const wanted = params.get('name');
    if (!wanted || !groups) return;
    const group = groups.find((item) => item.items.includes(wanted));
    if (group) { setName(wanted); setOpenGroup(group.name); setRowIndex(0); }
  }, [params, groups]);

  const visibleGroups = useMemo(() => {
    const normalized = search.trim().toLowerCase();
    return (groups ?? []).map((group) => ({ name: group.name, items: group.items.filter((item) => !normalized || item.toLowerCase().includes(normalized)) })).filter((group) => group.items.length > 0);
  }, [groups, search]);

  const rows: Row[] = (table?.rows ?? [])
    .map((cells, index) => ({ key: String(index), cells }))
    .filter((row) => !rowFilter.trim() || row.cells.join(' ').toLowerCase().includes(rowFilter.trim().toLowerCase()));
  const current = table?.rows[rowIndex] ?? table?.rows[0];
  const columns: Column<Row>[] = (table?.columns ?? []).map((header, index) => ({ key: header, header, render: (row) => row.cells[index] }));

  const isTariffs = name === 'Тарифы' || name === 'Льготники' || name === 'Категории льготников';

  const showPassport = async () => {
    if (!current?.[0]) return;
    try {
      setPassport((await api.directories.viewPassport(current[0])).value);
    } catch (reason) {
      setPassport(errorText(reason));
    }
  };

  return (
    <div className="split">
      <aside className="pane" style={{ width: 264 }}>
        <div style={{ padding: '12px 12px 8px' }}>
          <TextInput type="search" aria-label="Найти справочник" placeholder="Найти справочник" value={search} onChange={(event) => setSearch(event.target.value)} />
        </div>
        <div className="scroll" style={{ padding: '0 8px 12px' }}>
          <AccordionNav groups={visibleGroups} forceOpen={Boolean(search.trim())} openGroup={openGroup} onOpenGroup={setOpenGroup} itemKey={(item) => item} itemLabel={(item) => item} isSelected={(item) => item === name} onSelect={(item) => { setName(item); setRowIndex(0); setPassport(null); setRowFilter(''); }} />
        </div>
      </aside>

      <main className="main">
        <div className="bar">
          <h1 className="title">{name}</h1>
          <div style={{ flex: '1 1 220px', minWidth: 0 }}>
            <TextInput type="search" aria-label="Фильтр по записям" placeholder="Фильтр по записям" value={rowFilter} onChange={(event) => setRowFilter(event.target.value)} style={{ minHeight: 38 }} />
          </div>
        </div>
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        {name === 'Тарифы' ? <TariffsDirectory filter={rowFilter} /> : null}
        {name === 'Льготники' ? <BeneficiariesDirectory filter={rowFilter} /> : null}
        {name === 'Категории льготников' ? <CategoriesDirectory filter={rowFilter} /> : null}
        {!isTariffs && table?.placeholder && <div style={{ padding: '8px 16px' }}><Notice>Этот справочник появится после переноса данных из старой системы.</Notice></div>}
        {!isTariffs && <div className="split">
          <div className="main">
            {!table?.placeholder && <DataTable columns={columns} rows={rows} rowKey={(row) => row.key} onRowClick={(row) => { setRowIndex(Number(row.key)); setPassport(null); }} isSelected={(row) => Number(row.key) === rowIndex} empty={<span className="muted">Записей нет.</span>} />}
          </div>
          {!table?.placeholder && current && (
            <aside className="pane pane-right" aria-label="Запись" style={{ width: 320 }}>
              <div className="scroll stack pad">
                <h2 className="section-title">Запись</h2>
                <DefinitionRows columns={table?.columns ?? []} values={current} />
                {table?.sensitive && role === 'admin' && (
                  <div className="small muted" style={{ borderTop: '1px solid var(--line)', paddingTop: 10, lineHeight: 1.45 }}>
                    <div className="strong" style={{ color: 'var(--ink)', marginBottom: 4 }}>Паспорт</div>
                    {passport ? <div>{passport}</div> : <Button size="full" style={{ height: 36 }} onClick={() => void showPassport()}>Показать, запись в журнал</Button>}
                  </div>
                )}
                <p className="small muted">Справочники пока только для чтения. Абонентов можно менять в разделе «Абоненты».</p>
              </div>
            </aside>
          )}
        </div>}
      </main>
    </div>
  );
}

function DefinitionRows({ columns, values }: { columns: string[]; values: string[] }) {
  return (
    <>
      {columns.map((column, index) => (
        <div key={column}>
          <div className="field-label">{column}</div>
          <div>{values[index]}</div>
        </div>
      ))}
    </>
  );
}
