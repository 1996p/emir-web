import { useSearchParams } from 'react-router-dom';
import { api, useApi } from '@/api';
import { formatMoney } from '@/domain/billing';
import { formatPrice } from '@/domain/tariffs';
import type { DocumentData, DocumentLine, PrintDocument } from '@/domain/types';
import { Button, Notice } from '@/ui';

const vatLabel = (rate: number | null) => (rate === null ? 'Без НДС' : `${rate} %`);
const orDash = (value: string | null | undefined) => value || '—';
const volume = (line: DocumentLine) => (line.volume === null ? '—' : line.volume.toLocaleString('ru-RU', { maximumFractionDigits: 4 }));
const price = (line: DocumentLine) => (line.price === null ? '—' : formatPrice(line.price));

function Bank({ data }: { data: DocumentData }) {
  const { seller } = data;
  return (
    <table className="doc-table">
      <tbody>
        <tr><td colSpan={2}>{orDash(seller.bankName)}<div className="doc-small">Банк получателя</div></td><td>БИК</td><td>{orDash(seller.bik)}</td></tr>
        <tr><td colSpan={2}></td><td>Сч. №</td><td>{orDash(seller.corrAccount)}</td></tr>
        <tr><td>ИНН {seller.inn}</td><td>КПП {orDash(seller.kpp)}</td><td rowSpan={2}>Сч. №</td><td rowSpan={2}>{orDash(seller.account)}</td></tr>
        <tr><td colSpan={2}>{seller.name}<div className="doc-small">Получатель</div></td></tr>
      </tbody>
    </table>
  );
}

function Signs({ data, buyer }: { data: DocumentData; buyer?: boolean }) {
  return (
    <div className="doc-signs">
      <div>Руководитель <span className="doc-line" /> {orDash(data.seller.director)}</div>
      {buyer ? <div>Заказчик <span className="doc-line" /></div> : <div>Главный бухгалтер <span className="doc-line" /> {orDash(data.seller.chiefAccountant)}</div>}
    </div>
  );
}

const revisionNote = (data: DocumentData) => (data.revision > 1 ? `Исправление № ${data.revision - 1} от ${data.revisionDate}` : null);

function Receipt({ data }: { data: DocumentData }) {
  return (
    <>
      <h1 className="doc-title">Квитанция за {data.periodTitle.toLowerCase()}</h1>
      <p className="doc-center">№ {data.number} от {data.date}{revisionNote(data) ? `. ${revisionNote(data)}` : ''}</p>
      <p><b>Получатель:</b> {data.seller.name}, ИНН {data.seller.inn}, {data.seller.address}</p>
      <p><b>Реквизиты:</b> р/с {orDash(data.seller.account)}, {orDash(data.seller.bankName)}, БИК {orDash(data.seller.bik)}, к/с {orDash(data.seller.corrAccount)}</p>
      <p><b>Плательщик:</b> {data.buyer.name}, {data.buyer.address}. <b>Лицевой счёт:</b> {data.contract}</p>
      <table className="doc-table">
        <thead><tr><th>Услуга</th><th>Ед.</th><th>Объём</th><th>Тариф, ₽</th><th>Начислено, ₽</th><th>Долг по услуге, ₽</th></tr></thead>
        <tbody>
          {data.lines.map((line) => <tr key={line.service}><td>{line.service}</td><td>{line.unit}</td><td className="doc-right">{volume(line)}</td><td className="doc-right">{price(line)}</td><td className="doc-right">{formatMoney(line.amount)}</td><td className="doc-right">{formatMoney(line.debt)}</td></tr>)}
        </tbody>
        <tfoot><tr><th colSpan={4} className="doc-right">Начислено за месяц</th><th className="doc-right">{formatMoney(data.total)}</th><th className="doc-right">{formatMoney(data.debt)}</th></tr></tfoot>
      </table>
      {data.credit > 0 && <p>Переплата: {formatMoney(data.credit)} ₽</p>}
      <p className="doc-total">К оплате на дату формирования: {formatMoney(data.toPay)} ₽</p>
      <p>{data.toPayWords}</p>
      <p className="doc-small">Оплаченные после формирования суммы здесь не учтены.</p>
    </>
  );
}

function Bill({ data }: { data: DocumentData }) {
  return (
    <>
      <Bank data={data} />
      <h1 className="doc-title">Счёт № {data.number} от {data.date}</h1>
      {revisionNote(data) && <p className="doc-center">{revisionNote(data)}</p>}
      <p><b>Поставщик:</b> {data.seller.name}, ИНН {data.seller.inn}, КПП {orDash(data.seller.kpp)}, {data.seller.address}</p>
      <p><b>Покупатель:</b> {data.buyer.name}, ИНН {orDash(data.buyer.inn)}, {data.buyer.address}</p>
      <p><b>Основание:</b> договор № {data.contract}, {data.periodTitle.toLowerCase()}</p>
      <ServiceTable data={data} />
      <p className="doc-total">Итого к оплате: {formatMoney(data.total)} ₽</p>
      <p>{data.totalWords}</p>
      <Signs data={data} />
    </>
  );
}

function ServiceTable({ data }: { data: DocumentData }) {
  return (
    <table className="doc-table">
      <thead><tr><th>№</th><th>Услуга</th><th>Ед.</th><th>Кол-во</th><th>Цена, ₽</th><th>Сумма, ₽</th><th>В т.ч. НДС, ₽</th></tr></thead>
      <tbody>
        {data.lines.map((line, index) => <tr key={line.service}><td>{index + 1}</td><td>{line.service}, {data.periodTitle.toLowerCase()}</td><td>{line.unit}</td><td className="doc-right">{volume(line)}</td><td className="doc-right">{price(line)}</td><td className="doc-right">{formatMoney(line.amount)}</td><td className="doc-right">{data.vatRate === null ? 'Без НДС' : formatMoney(line.vat)}</td></tr>)}
      </tbody>
      <tfoot><tr><th colSpan={5} className="doc-right">Итого</th><th className="doc-right">{formatMoney(data.total)}</th><th className="doc-right">{data.vatRate === null ? 'Без НДС' : formatMoney(data.vatTotal)}</th></tr></tfoot>
    </table>
  );
}

function Act({ data }: { data: DocumentData }) {
  return (
    <>
      <h1 className="doc-title">Акт № {data.number} от {data.date}</h1>
      {revisionNote(data) && <p className="doc-center">{revisionNote(data)}</p>}
      <p><b>Исполнитель:</b> {data.seller.name}, ИНН {data.seller.inn}, КПП {orDash(data.seller.kpp)}, {data.seller.address}</p>
      <p><b>Заказчик:</b> {data.buyer.name}, ИНН {orDash(data.buyer.inn)}, {data.buyer.address}</p>
      <p><b>Основание:</b> договор № {data.contract}</p>
      <ServiceTable data={data} />
      <p>Услуги за {data.periodTitle.toLowerCase()} оказаны на сумму {formatMoney(data.total)} ₽. {data.totalWords}.</p>
      <p>Стороны претензий по объёму, качеству и срокам оказания услуг не имеют.</p>
      <Signs data={data} buyer />
    </>
  );
}

function Facture({ data }: { data: DocumentData }) {
  return (
    <>
      <h1 className="doc-title">Счёт-фактура № {data.number} от {data.date}</h1>
      <p className="doc-center">Исправление № {data.revision > 1 ? `${data.revision - 1} от ${data.revisionDate}` : '— от —'}</p>
      <p>Продавец: {data.seller.name}</p>
      <p>Адрес: {data.seller.address}</p>
      <p>ИНН/КПП продавца: {data.seller.inn}/{orDash(data.seller.kpp)}</p>
      <p>Грузоотправитель и его адрес: —</p>
      <p>Грузополучатель и его адрес: —</p>
      <p>К платёжно-расчётному документу № — от —</p>
      <p>Документ об отгрузке: акт № {orDash(data.actNumber)} от {data.date}</p>
      <p>Покупатель: {data.buyer.name}</p>
      <p>Адрес: {data.buyer.address}</p>
      <p>ИНН/КПП покупателя: {orDash(data.buyer.inn)}/{orDash(data.buyer.kpp)}</p>
      <p>Валюта: наименование, код: российский рубль, 643</p>
      <p>Идентификатор государственного контракта, договора (соглашения): —</p>
      <table className="doc-table doc-table-small">
        <thead><tr><th>Наименование услуги</th><th>Код вида</th><th>Ед. код</th><th>Ед. обозн.</th><th>Кол-во</th><th>Цена за ед. без НДС, ₽</th><th>Стоимость без НДС, ₽</th><th>Акциз</th><th>Ставка</th><th>НДС, ₽</th><th>Стоимость с НДС, ₽</th><th>Страна</th><th>Рег. № ТД</th></tr></thead>
        <tbody>
          {data.lines.map((line) => {
            const net = line.amount - line.vat;
            return (
              <tr key={line.service}>
                <td>{line.service}, {data.periodTitle.toLowerCase()}</td><td>—</td><td>{line.unitCode}</td><td>{line.unit}</td><td className="doc-right">{volume(line)}</td>
                <td className="doc-right">{line.volume ? formatPrice(net / 100 / line.volume) : '—'}</td><td className="doc-right">{formatMoney(net)}</td><td>без акциза</td>
                <td>{vatLabel(data.vatRate)}</td><td className="doc-right">{data.vatRate === null ? 'Без НДС' : formatMoney(line.vat)}</td><td className="doc-right">{formatMoney(line.amount)}</td><td>—</td><td>—</td>
              </tr>
            );
          })}
        </tbody>
        <tfoot><tr><th colSpan={6} className="doc-right">Всего к оплате</th><th className="doc-right">{formatMoney(data.total - data.vatTotal)}</th><th colSpan={2}>Х</th><th className="doc-right">{data.vatRate === null ? 'Без НДС' : formatMoney(data.vatTotal)}</th><th className="doc-right">{formatMoney(data.total)}</th><th colSpan={2}></th></tr></tfoot>
      </table>
      <Signs data={data} />
    </>
  );
}

function PrintForm({ document }: { document: PrintDocument }) {
  const { data, kind, status } = document;
  return (
    <section className="doc-page">
      {status !== 'active' && <p className="doc-replaced">Заменён исправленным документом</p>}
      {kind === 'receipt' && <Receipt data={data} />}
      {kind === 'bill' && <Bill data={data} />}
      {kind === 'act' && <Act data={data} />}
      {kind === 'facture' && <Facture data={data} />}
    </section>
  );
}

export function PrintDocumentsPage() {
  const [params] = useSearchParams();
  const ids = params.get('ids') ?? '';
  const { data, error } = useApi(() => api.documents.print(ids), [ids]);

  return (
    <div className="doc-screen">
      <div className="doc-toolbar">
        <span className="strong">Документов: {data?.length ?? 0}</span>
        <Button variant="primary" disabled={!data?.length} onClick={() => window.print()}>Печать</Button>
      </div>
      {error && <div className="pad"><Notice tone="warn">{error}</Notice></div>}
      {data?.map((document) => <PrintForm key={document.id} document={document} />)}
    </div>
  );
}
