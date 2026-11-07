import { Link } from 'react-router-dom';
import { api, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import type { ReadinessProblem } from '@/domain/types';
import { Notice } from '@/ui';

/** Куда вести оператора, чтобы устранить причину. Сопоставляем по тексту причины начисления. */
function fixFor(problem: string): { to: string; label: string } | null {
  const text = problem.toLowerCase();
  if (text.includes('показани')) return { to: '/readings', label: 'Ввести показания' };
  if (text.includes('общедомовой счётчик')) return { to: '/readings/house', label: 'Показания и привязка ОДПУ' };
  if (text.includes('норма теплопотребления дома')) return { to: '/directories?name=' + encodeURIComponent('Нормы теплопотребления'), label: 'Задать норму' };
  if (text.includes('не выбран тариф')) return { to: '/directories?name=' + encodeURIComponent('Тарифы'), label: 'Тарифы' };
  if (text.includes('цены тарифа')) return { to: '/directories?name=' + encodeURIComponent('Тарифы'), label: 'Цены тарифов' };
  if (text.includes('норматив') || text.includes('норма по услуге') || text.includes('процент месяца')) return { to: '/directories?name=' + encodeURIComponent('Тарифы'), label: 'Тарифы и нормативы' };
  return null;
}

export function ReadinessPage() {
  const { data, error } = useApi(() => api.period.readiness(), []);

  return (
    <div className="scroll stack pad" style={{ maxWidth: 820 }}>
      <div className="between" style={{ alignItems: 'center', flexWrap: 'wrap', gap: 8 }}>
        <h1 className="title">Готовность месяца{data ? `, ${data.period}` : ''}</h1>
        <Link to="/period" className="btn" style={{ width: 180 }}>К шагам месяца</Link>
      </div>
      {error && <Notice tone="warn">{error}</Notice>}
      {data && (
        <>
          {data.reopened && <Notice tone="warn">Месяц открыт на исправление (перезакрытие).</Notice>}
          {data.blockedLines === 0
            ? <Notice tone="ok">Всё готово к начислению: посчитается <strong>{formatMoney(data.wouldCharge)} ₽</strong> по {data.readyLines} услугам, абонентов {data.readyAbonents.toLocaleString('ru-RU')}. Строк с проблемами нет.</Notice>
            : <Notice>Посчитается <strong>{formatMoney(data.wouldCharge)} ₽</strong> по {data.readyLines} услугам. Не будут начислены <strong>{data.blockedLines}</strong> услуг у {data.blockedAbonents.toLocaleString('ru-RU')} абонентов — причины ниже.</Notice>}

          <section className="stack" style={{ gap: 8 }}>
            <h2 className="section-title">Общедомовые счётчики</h2>
            <div className="card" style={{ padding: 12 }}>
              <div className="between" style={{ alignItems: 'center', gap: 12 }}>
                <span>Показания за месяц внесены: <strong>{data.houseMetersWithReading} из {data.houseMetersTotal}</strong></span>
                <Link to="/readings/house" className="btn" style={{ width: 200 }}>Общедомовые счётчики</Link>
              </div>
            </div>
          </section>

          {data.problems.length > 0 && (
            <section className="stack" style={{ gap: 8 }}>
              <h2 className="section-title">Что мешает начислить</h2>
              {data.problems.map((problem) => <ProblemCard key={problem.problem} problem={problem} />)}
            </section>
          )}
        </>
      )}
    </div>
  );
}

function ProblemCard({ problem }: { problem: ReadinessProblem }) {
  const fix = fixFor(problem.problem);
  return (
    <div className="card" style={{ padding: 12 }}>
      <div className="between" style={{ alignItems: 'flex-start', gap: 12 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="strong">{problem.problem}</div>
          <div className="small muted" style={{ marginTop: 2 }}>
            Услуг: {problem.lines.toLocaleString('ru-RU')} · абонентов: {problem.abonents.toLocaleString('ru-RU')}
          </div>
          <div className="small muted" style={{ marginTop: 2 }}>
            Например: {problem.examples.join(', ')}{problem.abonents > problem.examples.length ? ' и другие' : ''}
          </div>
        </div>
        {fix && <Link to={fix.to} className="btn" style={{ width: 200, flexShrink: 0 }}>{fix.label}</Link>}
      </div>
    </div>
  );
}
