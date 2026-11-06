#!/usr/bin/env python3
"""Разбирает сохранённые карточки и журналы счетов. Работает только с локальными файлами.

В консоль выводятся счётчики и расхождения по номерам абонентов, без имён и адресов.
Запуск: parse_cards.py <папка>  -> <папка>/normalized/{abonents,operations}.jsonl и parse-report.json
"""
import collections
import glob
import gzip
import html
import json
import os
import re
import sys
from decimal import Decimal
from html.parser import HTMLParser

root = sys.argv[1]
cards = os.path.join(root, 'raw', 'cards')
out_dir = os.path.join(root, 'normalized')
os.makedirs(out_dir, mode=0o700, exist_ok=True)


class Rows(HTMLParser):
    """Строки таблиц с текстом ячеек и ссылками; вложенность таблиц не важна, нужны только плоские строки."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.rows, self.row, self.cell = [], None, None

    def handle_starttag(self, tag, attrs):
        if tag == 'tr':
            self.row = []
            self.rows.append(self.row)
        elif tag in ('td', 'th') and self.row is not None:
            self.cell = []
            self.row.append(self.cell)

    def handle_data(self, data):
        if self.cell is not None:
            self.cell.append(data)

    def text(self):
        return [[re.sub(r'\s+', ' ', ''.join(cell)).strip() for cell in row] for row in self.rows]


def read_rows(path):
    with gzip.open(path, 'rt', encoding='utf-8', errors='replace') as source:
        parser = Rows()
        parser.feed(source.read())
    return parser.text()


def money(text):
    text = text.replace(',', '').replace(' ', '').replace('\xa0', '')
    return Decimal(text) if text else Decimal(0)


PERIOD = re.compile(r'за (?:период )?(\d{4}-\d{2})')
AMOUNT = re.compile(r'\[\s*([\d.]+)\*([\d.]+)')
TIME = re.compile(r'^\d{4}-\d{2}-\d{2} ')


def parse_journal(path):
    rows = read_rows(path)
    operations, totals, number = [], {}, None
    for row in rows:
        if len(row) in (9, 11) and TIME.match(row[0]):
            incoming, outgoing, operator = (row[7], row[8], row[10]) if len(row) == 11 else (row[6], row[7], row[8])
            description = row[5]
            period = PERIOD.search(description)
            pair = AMOUNT.search(description)
            operations.append({
                'at': row[0], 'contract': row[1], 'service': row[3], 'doc': row[4], 'text': description,
                'in': str(money(incoming)), 'out': str(money(outgoing)), 'operator': operator,
                'period': period.group(1) if period else None,
                'price': pair.group(1) if pair else None, 'volume': pair.group(2) if pair else None,
            })
        elif len(row) >= 2 and row[0].endswith(':') and re.search(r'по счету|по отчету', row[0]):
            value = next((cell for cell in row[1:] if cell), '0')
            totals[row[0]] = str(money(value))
    return operations, totals


def parse_card(path):
    rows = read_rows(path)
    card = {'services': [], 'fields': {}}
    with gzip.open(path, 'rt', encoding='utf-8', errors='replace') as source:
        page = html.unescape(source.read())
    # Имя стоит в узле навигатора страницы, адрес в надписи с запретом переноса: от страны до квартиры.
    found = re.search(r'class="NavigatorNode">\s*([^<]+?)\s*<', page)
    card['name'] = found.group(1) if found else ''
    found = re.search(r'white-space:nowrap;" >([^<]+)<', page)
    card['address'] = re.sub(r'\s+', ' ', found.group(1)).strip() if found else ''
    # Заметка, документ о собственности и согласие на передачу персональных данных.
    found = re.search(r'name="\$V_Info"[^>]*>([^<]*)</textarea>', page)
    card['note'] = found.group(1).strip() if found else ''
    found = re.search(r'name="\$V_Document"[^>]*>([^<]*)</textarea>', page)
    card['ownership_document'] = found.group(1).strip() if found else ''
    checked = [re.search(r'value="([^"]*)"', item.group(0)) for item in re.finditer(r'<input[^>]*name="PersonalInfoPermission"[^>]*>', page) if 'checked' in item.group(0)]
    card['consent'] = checked[0].group(1) if checked and checked[0] else ''
    in_services = False
    labels = {'ID', 'Организация', 'Адрес', 'ФИО', 'Проживающие', 'Категория дома', 'Дополнительная информация'}
    for row in rows:
        if row[:2] == ['ID', 'Услуга']:
            in_services = True
            continue
        if in_services:
            if row and row[0] == 'Итого':
                card['total'] = row[4] if len(row) > 4 else None
                in_services = False
            elif len(row) >= 8 and row[0].isdigit():
                card['services'].append({'contract': row[0], 'service': row[1], 'state': row[2], 'account': row[3], 'balance': str(money(row[4])), 'tariff': row[5], 'price': row[6], 'volume': row[7]})
            continue
        for index in range(len(row) - 1):
            if row[index] in labels and row[index] not in card['fields']:
                card['fields'][row[index]] = row[index + 1]
    return card


def main():
    ids = sorted({os.path.basename(path).split('_')[0] for path in glob.glob(os.path.join(cards, '*_card.html.gz'))}, key=int)
    report = collections.Counter()
    problems = []
    with open(os.path.join(out_dir, 'abonents.jsonl'), 'w', encoding='utf-8') as abonents_file, open(os.path.join(out_dir, 'operations.jsonl'), 'w', encoding='utf-8') as operations_file:
        os.chmod(abonents_file.name, 0o600)
        os.chmod(operations_file.name, 0o600)
        for abonent in ids:
            card = parse_card(os.path.join(cards, f'{abonent}_card.html.gz'))
            card['id'] = abonent
            report['абонентов'] += 1
            journals = collections.defaultdict(list)
            for path in glob.glob(os.path.join(cards, f'{abonent}_journal_*.html.gz')):
                key = os.path.basename(path).split('_')[2].split('.')[0]
                journals[key].append(path)
            for key, paths in journals.items():
                operations, totals = [], {}
                for path in sorted(paths):
                    part, part_totals = parse_journal(path)
                    operations += part
                    totals = part_totals
                incoming = sum(Decimal(op['in']) for op in operations)
                outgoing = sum(Decimal(op['out']) for op in operations)
                balance = incoming - outgoing
                report['журналов'] += 1
                report['операций'] += len(operations)
                closing = totals.get('Итого баланс по счету:')
                if closing is None:
                    report['журнал без итога'] += 1
                    problems.append({'abonent': abonent, 'journal': key, 'problem': 'нет итога'})
                elif len(paths) == 1 and Decimal(closing) != balance:
                    report['журнал не сходится с итогом'] += 1
                    problems.append({'abonent': abonent, 'journal': key, 'problem': 'итог', 'sum': str(balance), 'total': closing})
                # Какому счёту карточки принадлежит журнал: страница счёта лежит рядом и содержит его номер.
                account_page = os.path.join(cards, f'{abonent}_account_{key}.html.gz')
                number = None
                if os.path.exists(account_page):
                    with gzip.open(account_page, 'rt', encoding='utf-8', errors='replace') as source:
                        found = re.search(r'RUB\.\d+\.[^\s<"\']+', source.read())
                    number = found.group(0) if found else None
                service = next((line for line in card['services'] if line['account'] == number), None)
                if service is None:
                    report['журнал без услуги в карточке'] += 1
                elif Decimal(service['balance']) != balance:
                    report['баланс карточки не равен журналу'] += 1
                    problems.append({'abonent': abonent, 'journal': key, 'problem': 'баланс', 'sum': str(balance), 'card': service['balance']})
                else:
                    report['баланс сошёлся'] += 1
                for op in operations:
                    op['abonent'] = abonent
                    op['account'] = number
                    operations_file.write(json.dumps(op, ensure_ascii=False) + '\n')
                    report[f"операция: {op['service'][:24]} / {op['doc'][:24]}"] += 1
                    if op['doc'] == 'Абон. плата' and not op['period']:
                        report['начисление без месяца'] += 1
            if not journals and card['services']:
                report['услуги есть, журналов нет'] += 1
            report['услуг'] += len(card['services'])
            abonents_file.write(json.dumps(card, ensure_ascii=False) + '\n')
    with open(os.path.join(root, 'parse-report.json'), 'w', encoding='utf-8') as output:
        json.dump({'report': report, 'problems': problems}, output, ensure_ascii=False, indent=1)
    os.chmod(output.name, 0o600)
    for name, value in report.most_common():
        print(value, name)
    print('примеры расхождений', problems[:6])


main()
