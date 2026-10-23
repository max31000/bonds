/** Тексты раздела «Freedom» (задача 60). */

export const FREEDOM_DATA_INCOMPLETE =
  'Нет курса валюты для части позиций или денег — стоимость в рублях и итог занижены. Суммы в валюте бумаги верны.';

export const FREEDOM_FX_NOTE =
  'Цены, стоимость и P&L — в валюте бумаги (код валюты указан). Рубли и доллары — оценка по курсам bonds (MOEX / ЦБ РФ), а не по курсу брокера. ' +
  'Класс бумаги (ETF / акция) определяется по названию — эвристика. Не инвестиционная рекомендация.';

export const FREEDOM_EMPTY_TITLE = 'Позиций нет';
export const FREEDOM_EMPTY_DESCRIPTION = 'Позиции и деньги появятся после синка (кнопка «Обновить данные»).';

export const FREEDOM_CONNECT_HINT =
  'После подключения ключей на «Позициях» появится вкладка «Freedom», а на «Обзоре» — KPI «Freedom» и «Капитал всего».';

export const FREEDOM_ASSET_CLASS_LABEL: Record<string, string> = {
  etf: 'ETF',
  stock: 'Акция',
  bond: 'Облигация',
  fund: 'Фонд',
  other: 'Другое',
};
