import { useState } from 'react';
import { api, useApi } from '@/api';
import { DataTable, Notice, type Column } from '@/ui';
import { UsersPanel } from './UsersPanel';

interface Row { key: string; cells: string[] }

export function AdminPage() {
  const { data: sections, error } = useApi(() => api.admin.sections(), []);
  const [selectedId, setSelectedId] = useState('roles');
  if (!sections) return error ? <div className="pad"><Notice tone="warn">{error}</Notice></div> : null;
  const current = sections.find((section) => section.id === selectedId) ?? sections[0]!;
  const groups = [...new Set(sections.map((section) => section.group))];
  const columns: Column<Row>[] = current.columns.map((header, index) => ({ key: header, header, render: (row) => row.cells[index] }));
  const rows: Row[] = current.rows.map((cells, index) => ({ key: String(index), cells }));

  return (
    <div className="split">
      <aside className="pane" style={{ width: 240 }}>
        <div className="scroll" style={{ padding: 8 }}>
          {groups.map((group) => (
            <div key={group} style={{ paddingTop: 8 }}>
              <div className="muted strong" style={{ fontSize: 12, padding: '0 8px 3px' }}>{group}</div>
              {sections.filter((section) => section.group === group).map((section) => (
                <button key={section.id} type="button" className="nav-item" style={{ display: 'flex', justifyContent: 'space-between', gap: 8, minHeight: 38, alignItems: 'center' }} aria-pressed={section.id === current.id} onClick={() => setSelectedId(section.id)}>
                  <span>{section.title}</span>
                </button>
              ))}
            </div>
          ))}
        </div>
      </aside>
      {current.id === 'users' ? <UsersPanel /> : <main className="main">
        <div className="bar">
          <div>
            <h1 className="title">{current.title}</h1>
            <p className="muted" style={{ fontSize: 13.5, marginTop: 2 }}>{current.description}</p>
          </div>
        </div>
        <DataTable columns={columns} rows={rows} rowKey={(row) => row.key} empty={<span className="muted">Записей нет.</span>} />
      </main>}
    </div>
  );
}
