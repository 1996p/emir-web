import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, errorText, useApi } from '@/api';
import { useRole } from '@/layout/role';
import { BeneficiariesDirectory, CategoriesDirectory } from './BenefitsDirectory';
import { TariffsDirectory } from './TariffsDirectory';
import { AccordionNav, Button, DataTable, Field, Notice, TextInput, type Column } from '@/ui';

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
  const [adding, setAdding] = useState(false);
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

  const matched: Row[] = (table?.rows ?? [])
    .map((cells, index) => ({ key: String(index), cells }))
    .filter((row) => !rowFilter.trim() || row.cells.join(' ').toLowerCase().includes(rowFilter.trim().toLowerCase()));
  // Большие справочники (десятки тысяч строк) показываются частью, остальное находится фильтром
  const ROW_LIMIT = 500;
  const rows = matched.slice(0, ROW_LIMIT);
  const current = table?.rows[rowIndex] ?? table?.rows[0];
  const columns: Column<Row>[] = (table?.columns ?? []).map((header, index) => ({ key: header, header, render: (row) => row.cells[index] }));

  const isTariffs = name === 'Тарифы' || name === 'Льготники' || name === 'Категории льготников';
  const canEdit = role === 'admin' || ((role === 'service' || role === 'accountant') && !table?.sensitive);

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
          <AccordionNav groups={visibleGroups} forceOpen={Boolean(search.trim())} openGroup={openGroup} onOpenGroup={setOpenGroup} itemKey={(item) => item} itemLabel={(item) => item} isSelected={(item) => item === name} onSelect={(item) => { setName(item); setRowIndex(0); setPassport(null); setRowFilter(''); setAdding(false); }} />
        </div>
      </aside>

      <main className="main">
        <div className="bar">
          <h1 className="title">{name}</h1>
          {!isTariffs && table?.editable && canEdit && <Button active={adding} onClick={() => setAdding((value) => !value)}>Добавить запись</Button>}
          <div style={{ flex: '1 1 220px', minWidth: 0 }}>
            <TextInput type="search" aria-label="Фильтр по записям" placeholder="Фильтр по записям" value={rowFilter} onChange={(event) => setRowFilter(event.target.value)} style={{ minHeight: 38 }} />
          </div>
        </div>
        {error && <div style={{ padding: '8px 16px' }}><Notice tone="warn">{error}</Notice></div>}
        {name === 'Тарифы' ? <TariffsDirectory filter={rowFilter} /> : null}
        {name === 'Льготники' ? <BeneficiariesDirectory filter={rowFilter} /> : null}
        {name === 'Категории льготников' ? <CategoriesDirectory filter={rowFilter} /> : null}
        {!isTariffs && table?.placeholder && <div style={{ padding: '8px 16px' }}><Notice>В старой системе этот справочник пуст или не переносился.</Notice></div>}
        {!isTariffs && <div className="split">
          <div className="main">
            {!table?.placeholder && matched.length > ROW_LIMIT && <div style={{ padding: '8px 16px' }}><Notice>Показаны первые {ROW_LIMIT} из {matched.length.toLocaleString('ru-RU')} записей. Уточните фильтр, чтобы найти нужную.</Notice></div>}
            {!table?.placeholder && <DataTable columns={columns} rows={rows} rowKey={(row) => row.key} onRowClick={(row) => { setRowIndex(Number(row.key)); setPassport(null); setAdding(false); }} isSelected={(row) => Number(row.key) === rowIndex} empty={<span className="muted">Записей нет.</span>} />}
          </div>
          {table && !table.placeholder && (adding || current) && (
            <aside className="pane pane-right" aria-label="Запись" style={{ width: 340 }}>
              <div className="scroll stack pad">
                {table.editable && canEdit
                  ? <RowEditor key={adding ? `new-${name}` : `${name}-${rowIndex}-${current?.join('|')}`} name={name} columns={table.columns} index={adding ? null : (table.rows[rowIndex] ? rowIndex : 0)} values={adding ? null : current ?? null} onDone={(added) => { setAdding(false); if (added) setRowIndex(table.rows.length); }} />
                  : <><h2 className="section-title">Запись</h2>{current && <DefinitionRows columns={table.columns} values={current} />}</>}
                {table?.sensitive && role === 'admin' && (
                  <div className="small muted" style={{ borderTop: '1px solid var(--line)', paddingTop: 10, lineHeight: 1.45 }}>
                    <div className="strong" style={{ color: 'var(--ink)', marginBottom: 4 }}>Паспорт</div>
                    {passport ? <div>{passport}</div> : <Button size="full" style={{ height: 36 }} onClick={() => void showPassport()}>Показать, запись в журнал</Button>}
                  </div>
                )}
                {!table.editable && <p className="small muted">Этот список собирается из карточек абонентов. Меняйте его в разделе «Абоненты».</p>}
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

/** Форма записи справочника: новая (index = null) или существующая. Любое изменение требует причины и попадает в журнал. */
function RowEditor({ name, columns, index, values, onDone }: { name: string; columns: string[]; index: number | null; values: string[] | null; onDone: (added: boolean) => void }) {
  const [cells, setCells] = useState<string[]>(() => columns.map((_, i) => values?.[i] ?? ''));
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const changed = values === null ? cells.some((cell) => cell.trim() !== '') : cells.some((cell, i) => cell.trim() !== (values[i] ?? ''));

  const run = async (action: () => Promise<void>, done: string, added = false) => {
    setError(null);
    setNote(null);
    try {
      await action();
      setReason('');
      setNote(done);
      onDone(added);
    } catch (reason_) {
      setError(errorText(reason_));
    }
  };

  return (
    <>
      <h2 className="section-title">{index === null ? 'Новая запись' : 'Запись'}</h2>
      {columns.map((column, i) => (
        <Field key={`${column}-${i}`} label={column}>{(id) => <TextInput id={id} value={cells[i]} onChange={(event) => setCells((current) => current.map((cell, j) => (j === i ? event.target.value : cell)))} />}</Field>
      ))}
      <Field label="Причина изменения">{(id) => <TextInput id={id} placeholder="Например, новый дом по акту" value={reason} onChange={(event) => setReason(event.target.value)} />}</Field>
      {error && <Notice tone="warn">{error}</Notice>}
      {note && <Notice tone="ok">{note}</Notice>}
      {index === null
        ? <Button variant="primary" size="full" disabled={!changed || !reason.trim()} onClick={() => void run(() => api.directories.addRow(name, cells, reason.trim()), 'Запись добавлена.', true)}>Добавить</Button>
        : (
          <>
            <Button variant="primary" size="full" disabled={!changed || !reason.trim()} onClick={() => void run(() => api.directories.editRow(name, index, values ?? [], cells, reason.trim()), 'Сохранено.')}>Сохранить</Button>
            {!confirmDelete
              ? <Button size="full" disabled={!reason.trim()} onClick={() => setConfirmDelete(true)}>Удалить запись</Button>
              : (
                <div className="stack" style={{ gap: 6 }}>
                  <Notice tone="warn">Удалить запись? Её содержимое останется в журнале.</Notice>
                  <div className="row">
                    <Button onClick={() => void run(() => api.directories.deleteRow(name, index, values ?? [], reason.trim()), 'Запись удалена.')}>Да, удалить</Button>
                    <Button onClick={() => setConfirmDelete(false)}>Отмена</Button>
                  </div>
                </div>
              )}
          </>
        )}
      <p className="small muted">Изменения с причиной записываются в журнал.</p>
    </>
  );
}
