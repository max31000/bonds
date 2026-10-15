import type { ReliabilityResponse, RiskSignals, ScoreComponent } from '../api/types';

/** Компонент скоркарты по умолчанию — «с данными»; поля перекрываются точечно. */
function component(partial: Partial<ScoreComponent> & Pick<ScoreComponent, 'key' | 'title' | 'layer'>): ScoreComponent {
  return {
    rawValue: null,
    rawDisplay: null,
    points: 0,
    maxPoints: 10,
    source: 'MOEX ISS: банк облигаций',
    missing: false,
    stale: false,
    ...partial,
  };
}

export const COMPONENTS: ScoreComponent[] = [
  component({ key: 'listing', title: 'Уровень листинга', layer: 'A', rawValue: 1, rawDisplay: 'листинг 1', points: 6, maxPoints: 6 }),
  component({
    key: 'broker_risk_level',
    title: 'Уровень риска у брокера',
    layer: 'A',
    rawDisplay: 'низкий риск',
    points: 10,
    maxPoints: 10,
    source: 'T-Invest: risk_level',
  }),
  component({
    key: 'interest_coverage',
    title: 'Покрытие процентов',
    layer: 'B',
    rawValue: 2.4,
    rawDisplay: '2200/2330 = 2.4×',
    points: 5,
    maxPoints: 10,
    source: 'ГИР БО ФНС (РСБУ, соло), 2023',
    stale: true,
  }),
  component({
    key: 'altman_z_em',
    title: 'Altman Z (EM)',
    layer: 'B',
    rawDisplay: 'нет данных: не хватает строк баланса (1200, 1500, 1600, 1370, 1300) или 2200',
    maxPoints: 8,
    source: 'ГИР БО ФНС (РСБУ, соло)',
    missing: true,
  }),
  component({
    key: 'spread_momentum',
    title: 'Динамика G-спреда',
    layer: 'C',
    rawValue: 0.0004,
    rawDisplay: '+4 б.п. к 20-дневной медиане',
    points: 8,
    maxPoints: 8,
    source: 'Банк облигаций: история',
  }),
];

/** Скоркарта «Red + Cheap» — мотивация цикла: ось Value независима от светофора. */
export const redCheapSignals: RiskSignals = {
  liquidity: 'Good',
  liquidityLabel: 'Высокая ликвидность, листинг 1',
  spread: 'Caution',
  gSpreadFraction: 0.04,
  spreadVsBasketMedianFraction: 0.01,
  reliability: 'Red',
  reliabilityReason: 'Красный: критический флаг «Прибыль не покрывает проценты».',
  score: 41.6,
  coverage: 0.78,
  components: COMPONENTS,
  criticalFlags: ['InterestUncovered'],
  warningFlags: ['PriceDrawdown'],
  valueVerdict: 'Cheap',
  valueMedianFraction: 0.03,
  valueThresholdFraction: 0.002,
};

export const greenFairSignals: RiskSignals = {
  ...redCheapSignals,
  reliability: 'Green',
  reliabilityReason: 'Зелёный: балл выше порога, критических флагов нет.',
  score: 88,
  criticalFlags: [],
  warningFlags: [],
  valueVerdict: 'Fair',
  gSpreadFraction: 0.0305,
  spreadVsBasketMedianFraction: 0.0005,
};

export function reliabilityResponse(overrides: Partial<ReliabilityResponse> = {}): ReliabilityResponse {
  return {
    secid: 'RU000GAZP01',
    isin: 'RU000GAZP001',
    name: 'Газпром капитал 5',
    emitentInn: '7736050003',
    emitentTitle: 'Газпром',
    riskSignals: redCheapSignals,
    disclaimer: '',
    ...overrides,
  };
}
