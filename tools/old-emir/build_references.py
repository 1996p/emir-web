#!/usr/bin/env python3
"""Собирает справочники старой системы в load/references.json для команды import-references.

Берутся скачанные списки как есть. В консоль выводятся только названия справочников и число строк.
Запуск: build_references.py <папка>
"""
import json
import os
import re
import sys

root = sys.argv[1]
raw = os.path.join(root, 'raw')

# Название в новом ЕМИР -> название пункта меню старой системы (если отличается)
OLD_NAMES = {
    'Общедомовые счётчики': 'Общедомовые счетчики',
    'Абоненты ЖКХ': 'Абоненты',
    'Порядок расчётов': 'Порядок расчетов',
    'Счета для лимитов': 'Список счетов для лимитов',
    'Проводки для лимитов': 'Список проводок для лимитов',
    'Тарифы: счётчик и среднее': 'Тарифы: счетчик - по среднему',
    'Тарифы: норматив и ПК': 'Тарифы: норматив - ПК',
    'Нормы по услугам': 'Нормы потребления по услугам',
    'Нормы по тарифу': 'Нормы потребления по тарифу',
    'Нормы по заказу': 'Нормы потребления по заказу',
    'Связка услуг и единиц': 'Связка услуг и единиц измерения',
}
# Справочники с персональными данными: в интерфейсе помечаются, паспорт только администратору
SENSITIVE = {'Абоненты', 'Абоненты ЖКХ', 'Проживающие', 'История проживающих', 'Список договоров', 'Льготники',
             'История льготников (Краснозерское)', 'Дополнительные суммы', 'Нормы по заказу', 'Данные о квартирах', 'Счета для лимитов'}
# Эти справочники новая система показывает из своих данных, их не перезаписываем
OWN = {'Люди', 'Организации', 'Лицевые счета', 'Услуги', 'Квартирные счётчики', 'Тарифы', 'Льготники', 'Категории льготников'}


def jsonl(path):
    with open(path, encoding='utf-8') as source:
        return [json.loads(line) for line in source if line.strip()]


def clean_head(title):
    return re.sub(r'[ ↑↓»«]+$', '', title).strip()


inventory = jsonl(os.path.join(root, 'inventory.jsonl'))
sources = {}
for item in inventory:
    if item['kind'] == 'query':
        slug = item['path'].replace('.csp', '').replace('.cls', '')
        slug = re.sub(r'[^A-Za-z0-9]+', '_', slug)
        sources.setdefault(item['name'], []).append(('csp', slug, item['section']))
    elif item['kind'] in ('zen', 'slow'):
        slug = 'zen_' + re.sub(r'[^A-Za-zА-Яа-яЁё0-9]+', '_', item['section'] + '_' + item['name'])
        sources.setdefault(item['name'], []).append(('zen', slug, item['section']))


def load_csp(slug):
    state_path = os.path.join(raw, f'{slug}.state.json')
    data_path = os.path.join(raw, f'{slug}.jsonl')
    if not os.path.exists(state_path) or not os.path.exists(data_path):
        return None
    head = [clean_head(title) for title in json.load(open(state_path, encoding='utf-8'))['head']]
    rows = [row['cells'] for row in jsonl(data_path)]
    width = max([len(head)] + [len(row) for row in rows]) if rows else len(head)
    head = head + [''] * (width - len(head))
    keep = [index for index in range(width) if head[index] or any(index < len(row) and row[index] for row in rows)]
    return [head[index] or f'Колонка {index + 1}' for index in keep], [[(row[index] if index < len(row) else '') for index in keep] for row in rows]


def load_zen(slug):
    state_path = os.path.join(raw, f'{slug}.state.json')
    data_path = os.path.join(raw, f'{slug}.jsonl')
    if not os.path.exists(state_path) or not os.path.exists(data_path):
        return None
    state = json.load(open(state_path, encoding='utf-8'))
    columns = [column for column in state.get('columns', []) if not column.get('hidden')]
    head_titles = [title for title in state.get('head', []) if title]
    names = [clean_head(head_titles[index]) if index < len(head_titles) else column['name'] for index, column in enumerate(columns)]
    rows = [row['cells'][2:2 + len(columns)] for row in jsonl(data_path)]
    rows = [row for row in rows if any(cell for cell in row)]
    return names, rows


def load_split(name, tag, columns_key='columns'):
    """Списки, скачанные кусками: повторы убираются по номеру записи."""
    data_path = os.path.join(raw, f'zensplit_{name}_{tag}.jsonl' if tag else f'zensplit_{name}.jsonl')
    state_path = data_path.replace('.jsonl', '.state.json')
    if not os.path.exists(data_path):
        return None
    columns = json.load(open(state_path, encoding='utf-8'))[columns_key]
    seen, rows = set(), []
    for row in jsonl(data_path):
        key = row['cells'][0] if row['cells'] else row.get('value')
        if key in seen:
            continue
        seen.add(key)
        rows.append(row['cells'][:len(columns)])
    return columns, rows


catalog_names = []
catalog = open(os.path.join(os.path.dirname(__file__), '..', '..', 'backend', 'Emir.Api', 'Domain', 'Catalog.cs'), encoding='utf-8').read()
block = catalog[catalog.index('Directories ='):catalog.index('RoleMatrix')]
for group in re.findall(r'new\("[^"]+", \[(.*?)\]\)', block, re.S):
    catalog_names += re.findall(r'"([^"]+)"', group)

tables, missing = [], []
for name in catalog_names:
    if name in OWN:
        continue
    loaded = None
    if name == 'История проживающих':
        loaded = load_split('История_проживающих', 'id')
    elif name.startswith('История льготников'):
        path = os.path.join(raw, 'benefit-history.jsonl')
        if os.path.exists(path):
            state = json.load(open(os.path.join(raw, 'benefit-history.state.json'), encoding='utf-8'))
            seen, rows = set(), []
            for row in jsonl(path):
                if row['value'] in seen:
                    continue
                seen.add(row['value'])
                rows.append(row['cells'][:len(state['columns'])])
            loaded = state['columns'], rows
    else:
        old = OLD_NAMES.get(name, name)
        # Пункт из раздела «Справочники» важнее одноимённого из других разделов
        prefer = 'Абонент' if name == 'Абоненты ЖКХ' else 'Справочник'
        for kind, slug, section in sorted(sources.get(old, []), key=lambda item: not item[2].startswith(prefer)):
            loaded = load_csp(slug) if kind == 'csp' else load_zen(slug)
            if loaded and loaded[1]:
                break
    # Старые списки улиц, домов и квартир без фильтра пусты: собираются из данных о домах и квартирах
    if name in ('Улицы', 'Дома', 'Квартиры'):
        houses = load_zen('zen_Справочники_Данные_о_домах')
        flats = load_zen('zen_Справочники_Данные_о_квартирах')
        if name == 'Улицы' and houses:
            h = houses[0]
            pairs = sorted({(row[h.index('Город')], row[h.index('Улица')]) for row in houses[1]})
            loaded = ['Населённый пункт', 'Улица'], [list(pair) for pair in pairs]
        elif name == 'Дома' and houses:
            h = houses[0]
            keep = [title for title in ('ID', 'Город', 'Улица', 'Дом', 'Котельная', 'Категория дома', 'Материал стен') if title in h]
            loaded = keep, [[row[h.index(title)] for title in keep] for row in houses[1]]
        elif name == 'Квартиры' and flats:
            loaded = flats
    if name == 'Заказы' or (loaded and len(loaded[1]) == 1 and not any(cell.strip() for cell in loaded[1][0][1:])):
        loaded = None
    if not loaded or not loaded[1]:
        missing.append(name)
        continue
    columns, rows = loaded
    tables.append({'name': name, 'columns': columns, 'rows': rows, 'sensitive': name in SENSITIVE})

out = os.path.join(root, 'load', 'references.json')
with open(out, 'w', encoding='utf-8') as output:
    json.dump(tables, output, ensure_ascii=False)
os.chmod(out, 0o600)
for table in tables:
    print(f"{len(table['rows']):6} {table['name']}")
print('без данных:', ', '.join(missing))
print('всего справочников', len(tables), 'размер', os.path.getsize(out) // 1024, 'КБ')
