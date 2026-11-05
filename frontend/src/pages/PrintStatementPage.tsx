import { useParams } from 'react-router-dom';
import { api, useApi } from '@/api';
import { balanceOf, formatBalance, formatMoney } from '@/domain/billing';
import { Button, Notice } from '@/ui';

/** Справка о состоянии счёта на сегодня. Это не учётный документ: без номера и без записи в базу. */
export function PrintStatementPage() {
  const { id = '' } = useParams();
  const { data: abonent, error } = useApi(() => api.abonents.get(id), [id]);
  const { data: profile } = useApi(() => api.documents.profile(), []);
  const today = new Date().toLocaleDateString('ru-RU');

  return (
    <div className="doc-screen">
      <div className="doc-toolbar">
        <span className="strong">Справка</span>
        <Button variant="primary" disabled={!abonent} onClick={() => window.print()}>Печать</Button>
      </div>
      {error && <div className="pad"><Notice tone="warn">{error}</Notice></div>}
      {abonent && (
        <section className="doc-page">
          <p>{profile?.name}{profile?.inn ? `, ИНН ${profile.inn}` : ''}</p>
          <p>{profile?.address}</p>
          <h1 className="doc-title">Справка о состоянии лицевого счёта</h1>
          <p className="doc-center">на {today}</p>
          <p><b>Абонент:</b> {abonent.name}</p>
          <p><b>Адрес:</b> {[abonent.place, abonent.street + (abonent.flat ? `, кв. ${abonent.flat}` : '')].filter(Boolean).join(', ')}</p>
          <p><b>Лицевой счёт:</b> {abonent.account}</p>
          <table className="doc-table">
            <thead><tr><th>Услуга</th><th>Состояние</th><th>Долг, ₽</th></tr></thead>
            <tbody>
              {abonent.services.map((line) => <tr key={line.id}><td>{line.name}</td><td>{line.state}</td><td className="doc-right">{formatMoney(line.debt)}</td></tr>)}
            </tbody>
          </table>
          {abonent.credit > 0 && <p>Переплата: {formatMoney(abonent.credit)} ₽</p>}
          <p className="doc-total">Остаток по счёту: {formatBalance(balanceOf(abonent))} ₽</p>
          <p className="doc-small">Минус означает долг, плюс переплату.</p>
          <div className="doc-signs"><div>Выдал <span className="doc-line" /></div><div>Дата {today}</div></div>
        </section>
      )}
    </div>
  );
}
