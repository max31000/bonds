import type { BasketState, CapitalBasket } from '../api/types';
import { formatMoney, formatRub } from './format';

/**
 * Корзины (задача 62). Бэкенд и API работают в ДОЛЯХ (0.15 = 15%); проценты существуют только здесь и в полях ввода формы.
 * Конверсия — ровно в двух функциях ниже, больше нигде не умножаем и не делим на 100.
 */

/** Доля → проценты для поля ввода/показа: 0.15 → 15 (округление до 4 знаков убирает артефакты плавающей точки). */
export function fractionToPercent(fraction: number): number {
  return Math.round(fraction * 100 * 10_000) / 10_000;
}

/** Проценты из поля ввода → доля для API: 15 → 0.15 (6 знаков — точность DECIMAL(9,6) на бэкенде). */
export function percentToFraction(percent: number): number {
  return Math.round((percent / 100) * 1_000_000) / 1_000_000;
}

/** «15,0%» — доля как проценты, 1 знак. */
export function formatWeight(fraction: number | null | undefined): string {
  if (fraction === null || fraction === undefined || Number.isNaN(fraction)) return '—';
  return `${fractionToPercent(fraction).toFixed(1).replace('.', ',')}%`;
}

/** Отклонение в процентных пунктах со знаком: −0.03 → «−3,0 п.п.». */
export function formatDrift(drift: number | null | undefined): string {
  if (drift === null || drift === undefined || Number.isNaN(drift)) return '—';
  const pp = fractionToPercent(drift);
  const sign = pp > 0 ? '+' : pp < 0 ? '−' : '';
  return `${sign}${Math.abs(pp).toFixed(1).replace('.', ',')} п.п.`;
}

/** Сумма в валюте портфеля: рубли — «1 234 ₽», доллары — «1 234 USD». */
export function formatPortfolioMoney(value: number | null | undefined, currency: string): string {
  return currency === 'RUB' ? formatRub(value) : formatMoney(value, currency, 0);
}

export const BASKET_STATE_LABEL: Record<BasketState, string> = {
  ok: 'В норме',
  below: 'Ниже цели',
  above: 'Выше цели',
  hardBelow: 'Сильно ниже цели',
  hardAbove: 'Сильно выше цели',
  noTarget: 'Вне целей',
};

export const BASKET_STATE_COLOR: Record<BasketState, string> = {
  ok: 'green',
  below: 'yellow',
  above: 'yellow',
  hardBelow: 'red',
  hardAbove: 'red',
  noTarget: 'gray',
};

/** Сумма целей сходится к 100% (допуск 0.0001 — как на бэкенде). Нет целей вовсе — проверять нечего. */
export function targetsSumOk(fractions: Array<number | null>): boolean {
  const set = fractions.filter((f): f is number => f !== null);
  return set.length === 0 || Math.abs(set.reduce((a, b) => a + b, 0) - 1) <= 0.0001;
}

/**
 * Геометрия полосы «цель/факт/допуск» в долях трека 0..1. Масштаб — до большего из (цель + жёсткий допуск, факт) с запасом, чтобы
 * факт и допуск всегда помещались. Все входы — ДОЛИ.
 */
export function barGeometry(basket: Pick<CapitalBasket, 'weight' | 'target' | 'band' | 'hardBand'>): {
  actual: number;
  target: number | null;
  bandFrom: number;
  bandTo: number;
  hardFrom: number;
  hardTo: number;
} {
  const target = basket.target;
  const band = basket.band ?? 0;
  const hard = basket.hardBand ?? 0;
  const scaleMax = Math.max(0.05, basket.weight, (target ?? 0) + hard) * 1.1;
  const at = (v: number) => Math.min(1, Math.max(0, v / scaleMax));
  const t = target ?? 0;
  return {
    actual: at(basket.weight),
    target: target === null ? null : at(target),
    bandFrom: at(t - band),
    bandTo: at(t + band),
    hardFrom: at(t - hard),
    hardTo: at(t + hard),
  };
}
