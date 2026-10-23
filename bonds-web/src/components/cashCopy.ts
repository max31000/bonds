/** Тексты блока «Деньги на счетах» (задача 61). */

export const CASH_BROKER_LABEL: Record<string, string> = {
  tinvest: 'T-Invest',
  freedom: 'Freedom',
};

export const CASH_MOVEMENT_LABEL: Record<string, string> = {
  deposit: 'Пополнение',
  withdrawal: 'Вывод',
};

export const CASH_DATA_INCOMPLETE = 'Нет курса валюты — рублёвая оценка части денег занижена. Суммы в валюте верны.';

export const CASH_NOTE =
  'Свободные рубли — без валюты: брокерский остаток уже включает валюту в пересчёте на рубли, поэтому валюта показана отдельными строками. ' +
  'Рубли за валюту — оценка по курсам bonds. Не инвестиционная рекомендация.';
