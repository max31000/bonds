import { useEffect, useRef, useState } from 'react';
import {
  Title,
  Stack,
  Paper,
  Text,
  Badge,
  Alert,
  Loader,
  Center,
  Group,
  Button,
  Table,
  TextInput,
  ActionIcon,
  Collapse,
  UnstyledButton,
  SegmentedControl,
  Switch,
} from '@mantine/core';
import { Page } from '@mvv42/kit';
import { useSearchParams } from 'react-router-dom';
import { useRecommendationsStore } from '../store/useRecommendationsStore';
import { useWatchlistStore } from '../store/useWatchlistStore';
import { useRelativeValueStore } from '../store/useRelativeValueStore';
import { Disclaimer } from '../components/Disclaimer';
import { ReplacementBreakdown } from '../components/ReplacementBreakdown';
import { horizonCaption, toBreakdownData } from '../utils/replacementBreakdown';
import { getReplacementHorizonYears, useSettingsStore } from '../store/useSettingsStore';
import { MarketComparator } from '../components/MarketComparator';
import { BasketConstructor } from '../components/BasketConstructor';
import { RelativeValueBadge } from '../components/RelativeValueBadge';
import { RiskSignalBadges, RiskSignalsCaption } from '../components/RiskSignalBadges';
import { ReliabilityFilterControl } from '../components/ReliabilityFilterControl';
import { toReliabilityQuery, type ReliabilityFilterValue } from '../utils/reliabilityFilter';
import { fetchReplacementCandidates, postReplacement } from '../api/recommendations';
import { postMaterialize } from '../api/universe';
import { formatRub, formatPercent, formatNumber, formatDate, formatHorizon } from '../utils/format';
import type {
  ComparisonRow,
  DurationScope,
  MaterializeResponse,
  ReplacementCandidate,
  ReplacementCandidatesMode,
  ReplacementResponse,
} from '../api/types';


/**
 * Окно фильтра «похожая дюрация» на кандидатах, ГОДЫ (±1.5 г.). Фильтрация клиентская, по уже
 * загруженному списку кандидатов, отдельного эндпоинта нет. Сравниваются дюрации МАКОЛЕЯ с обеих
 * сторон: `candidate.durationYears` — MOEX DURATION банка (= Маколей), `holdDurationYears` —
 * `ComparisonRow.macaulayDurationYears` позиции (не модифицированная — другая мера).
 */
const ComparableDurationWindowYears = 1.5;

/** Строка «вне сравнения» — floater/indexed/dataIncomplete не участвуют в ранжировании доходности (spec §6). */
function OutOfComparisonRow({ row }: { row: ComparisonRow }) {
  return (
    <Paper withBorder p="sm" radius="md" data-testid={`out-of-comparison-${row.positionId}`}>
      <Group justify="space-between">
        <Text size="sm">{row.name ?? row.issuer ?? `Позиция #${row.positionId}`}</Text>
        <Badge size="sm" color="gray" variant="outline">
          вне сравнения
        </Badge>
      </Group>
    </Paper>
  );
}

/** Одна строка кандидата-замены (mode=market/rv) — имя, доходность, дюрация, риск-бейджи. */
function ReplacementCandidateRow({
  candidate,
  taxRateFraction,
  onSelect,
  isSelected,
  isLoading,
}: {
  candidate: ReplacementCandidate;
  /** ДОЛЯ НДФЛ, учтённого в `afterTaxYieldFraction` (из ответа бэкенда); нет — подпись про налог не показываем. */
  taxRateFraction: number | null;
  onSelect: (secid: string) => void;
  isSelected: boolean;
  isLoading: boolean;
}) {
  // Задача 50: главная цифра — доходность к худшему из погашения/оферты ПОСЛЕ налога (по ней ранжирует бэкенд);
  // сырая биржевая YTM — мелко рядом. Нет ранга (rv-кандидат без банк-записи) — прежняя сырая цифра.
  const hasAfterTax = candidate.afterTaxYieldFraction !== null;
  const basisLabel =
    candidate.yieldBasis === 'offer'
      ? candidate.offerDate
        ? `к оферте ${formatDate(candidate.offerDate)}`
        : 'к оферте'
      : candidate.yieldBasis === 'maturity'
        ? 'к погашению'
        : null;
  return (
    // div[role=button], а не <button>: внутри строки живёт светофор-кнопка (задача 47), вложенные <button> — невалидный HTML.
    <UnstyledButton
      component="div"
      role="button"
      tabIndex={isLoading ? -1 : 0}
      aria-disabled={isLoading}
      onClick={() => {
        if (!isLoading) onSelect(candidate.secid);
      }}
      onKeyDown={(event: React.KeyboardEvent) => {
        if (!isLoading && event.target === event.currentTarget && (event.key === 'Enter' || event.key === ' ')) {
          event.preventDefault();
          onSelect(candidate.secid);
        }
      }}
      w="100%"
      data-testid={`replace-candidate-${candidate.secid}`}
    >
      <Paper
        withBorder
        p="xs"
        radius="md"
        style={isSelected ? { borderColor: 'var(--mvv-accent)' } : undefined}
      >
        <Group justify="space-between" wrap="wrap" gap={4}>
          <Text size="sm" fw={600}>
            {candidate.name}
          </Text>
          <Text size="sm" c="teal" fw={600} data-testid={`replace-candidate-yield-${candidate.secid}`}>
            {formatPercent(hasAfterTax ? candidate.afterTaxYieldFraction : candidate.yieldFraction)}
          </Text>
        </Group>
        {hasAfterTax && (
          <Text size="xs" c="dimmed" data-testid={`replace-candidate-yield-caption-${candidate.secid}`}>
            {basisLabel ?? 'к погашению'}
            {taxRateFraction !== null && <>, налог {formatPercent(taxRateFraction)} учтён</>}
            {' · '}биржевая YTM {formatPercent(candidate.yieldFraction)}
          </Text>
        )}
        <Group gap={6} wrap="wrap" mb={4}>
          <Text size="xs" c="dimmed">
            дюрация {candidate.durationYears !== null ? `${formatNumber(candidate.durationYears, 1)} лет` : '—'}
          </Text>
          {candidate.offerDate && (
            <Badge size="xs" color="grape" variant="outline" data-testid={`replace-candidate-offer-${candidate.secid}`}>
              оферта {formatDate(candidate.offerDate)}
            </Badge>
          )}
        </Group>
        <RiskSignalBadges signals={candidate.riskSignals} testIdSuffix={candidate.secid} lookup={candidate.secid} />
      </Paper>
    </UnstyledButton>
  );
}

const DURATION_SCOPE_OPTIONS: { label: string; value: DurationScope }[] = [
  { label: 'похожий срок', value: 'bucket' },
  { label: 'весь рынок', value: 'all' },
];

const MODE_OPTIONS: { label: string; value: ReplacementCandidatesMode | 'search' }[] = [
  { label: 'доходные рынка', value: 'market' },
  { label: 'дешёвые соседи (RV)', value: 'rv' },
  { label: 'поиск', value: 'search' },
];

/**
 * Панель подбора замены на карточке слабой позиции (задача 37 переносит карточку
 * выгоды под выбранную строку + добавляет фильтр «похожая дюрация») — три режима: «доходные
 * рынка»/«дешёвые соседи (RV)» читают банк-кандидатов через GET /replacement-candidates
 * и рендерят их с риск-сигналами; «поиск» переиспользует существующий <see>MarketComparator</see>
 * без изменений интерфейса. Клик по банк-кандидату материализует бумагу
 * (POST /universe/{secid}/materialize, тот же путь, что MarketComparator.handleSelect/ручное
 * добавление в BasketConstructor) и считает точную выгоду через POST /analytics/replacement —
 * результат рендерится тем же <see>ReplacementBreakdown</see>, что MarketComparator,
 * непосредственно под строкой выбранного кандидата (было — после всего списка).
 * Отказ GET /replacement-candidates не роняет карточку/страницу — просто показывает ошибку в панели.
 * <para><b>holdDurationYears</b> — дюрация заменяемой позиции
 * (<c>ComparisonRow.macaulayDurationYears</c> — Маколей, как и у кандидатов банка), нужна фильтру
 * «похожая дюрация» для окна вокруг неё.</para>
 */
function ReplacementPanel({
  holdPositionId,
  holdDurationYears,
}: {
  holdPositionId: number;
  holdDurationYears: number | null;
}) {
  const [mode, setMode] = useState<ReplacementCandidatesMode | 'search'>('market');
  const [candidates, setCandidates] = useState<ReplacementCandidate[]>([]);
  // Дефолт true — компонент всегда стартует с загрузки кандидатов дефолтного режима "market" (тот
  // же приём, что MarketComparator.isSearchLoading): "начало загрузки" сигнализируется синхронно из
  // handleModeChange (реальный обработчик события), НЕ из тела эффекта ниже — react-hooks/
  // set-state-in-effect запрещает синхронный setState прямо в теле эффекта.
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Клиентский фильтр «похожая дюрация (±ComparableDurationWindowYears лет)» —
  // дефолт ВЫКЛ (план: не усекать список неожиданно для пользователя, который его ещё не открывал).
  const [durationFilterEnabled, setDurationFilterEnabled] = useState(false);

  // Задача 50: фильтр «надёжность не хуже уровня» — СЕРВЕРНЫЙ (`reliability=` в запросе): лимит 20 применяется уже к
  // подходящим по светофору, а не к топ-20 сырой выдачи (иначе «только 🟢» мог дать 0 при зелёных дальше по рынку).
  // Дефолт 'all' (не усекать список неожиданно).
  const [reliabilityFilter, setReliabilityFilter] = useState<ReliabilityFilterValue>('all');

  // Задача 50: сопоставимость по сроку — «похожий срок» (бакет дюрации Маколея позиции ±1 соседний, дефолт бэкенда)
  // или «весь рынок». Только mode=market (RV-кандидаты и так из корзины позиции).
  const [durationScope, setDurationScope] = useState<DurationScope>('bucket');
  // Мета последнего ответа: ПРИМЕНЁННЫЙ scope (откат на «весь рынок» при неизвестной дюрации позиции), бакет позиции, ставка НДФЛ.
  const [responseMeta, setResponseMeta] = useState<{
    durationScope: DurationScope | null;
    positionDurationBucket: string | null;
    taxRateFraction: number | null;
  }>({ durationScope: null, positionDurationBucket: null, taxRateFraction: null });

  const [selectedSecid, setSelectedSecid] = useState<string | null>(null);
  const [isSelecting, setIsSelecting] = useState(false);
  const [selectError, setSelectError] = useState<string | null>(null);
  const [materialized, setMaterialized] = useState<MaterializeResponse | null>(null);
  const [replacement, setReplacement] = useState<ReplacementResponse | null>(null);

  // Карточка выгоды должна попадать во вьюпорт после раскрытия (строка может
  // быть у нижнего края списка) — scrollIntoView с block:'nearest' не скроллит, если уже видна.
  const resultRef = useRef<HTMLDivElement>(null);

  // generation-токен гонки клика по кандидату — клик по строке A запускает
  // postMaterialize/postReplacement, но клик по строке B до резолва A должен инвалидировать
  // результат A: без токена более поздний resolve A перезаписывал бы materialized/replacement уже
  // выбранной B (тот же класс бага, что cancelled-флаг в GET-эффекте кандидатов выше). Каждый вызов
  // handleSelectCandidate (включая toggle-off повторным кликом) получает новый id; перед каждым
  // set-ом результата проверяем, что id всё ещё актуален.
  const selectRequestIdRef = useRef(0);

  // Настройки (горизонт замены) нужны фоном; ensureLoaded не плодит повторных GET при многих карточках.
  const ensureSettingsLoaded = useSettingsStore((s) => s.ensureLoaded);
  useEffect(() => {
    void ensureSettingsLoaded();
  }, [ensureSettingsLoaded]);

  useEffect(() => {
    if (mode === 'search') return;
    let cancelled = false;

    fetchReplacementCandidates(holdPositionId, mode, {
      reliability: toReliabilityQuery(reliabilityFilter),
      durationScope: mode === 'market' ? durationScope : undefined,
    })
      .then((response) => {
        if (cancelled) return;
        setCandidates(response.candidates);
        setResponseMeta({
          durationScope: response.durationScope ?? null,
          positionDurationBucket: response.positionDurationBucket ?? null,
          taxRateFraction: response.afterTaxRateFraction ?? null,
        });
        setIsLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setLoadError(err instanceof Error ? err.message : 'Не удалось загрузить кандидатов');
        setIsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [mode, holdPositionId, reliabilityFilter, durationScope]);

  useEffect(() => {
    if (!isSelecting && !selectError && materialized && replacement) {
      resultRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
  }, [isSelecting, selectError, materialized, replacement]);

  const handleModeChange = (value: string) => {
    const nextMode = value as ReplacementCandidatesMode | 'search';
    setMode(nextMode);
    setCandidates([]);
    setSelectedSecid(null);
    setMaterialized(null);
    setReplacement(null);
    setSelectError(null);
    setLoadError(null);
    setIsLoading(nextMode !== 'search');
  };

  /** Смена серверного фильтра (надёжность / срок): список перезапрашивается, выбранный кандидат и его карточка сбрасываются. */
  const resetForRefetch = () => {
    selectRequestIdRef.current += 1;
    setCandidates([]);
    setSelectedSecid(null);
    setMaterialized(null);
    setReplacement(null);
    setSelectError(null);
    setLoadError(null);
    setIsLoading(true);
  };

  const handleReliabilityChange = (value: ReliabilityFilterValue) => {
    if (value === reliabilityFilter) return;
    resetForRefetch();
    setReliabilityFilter(value);
  };

  const handleDurationScopeChange = (value: DurationScope) => {
    if (value === durationScope) return;
    resetForRefetch();
    setDurationScope(value);
  };

  const handleSelectCandidate = async (secid: string) => {
    if (selectedSecid === secid) {
      // Повторный клик по уже выбранной строке сворачивает карточку выгоды.
      // инкремент токена инвалидирует уже летящий запрос выбора этой же строки — иначе
      // его поздний resolve мог бы снова раскрыть только что свёрнутую карточку.
      selectRequestIdRef.current += 1;
      setSelectedSecid(null);
      setMaterialized(null);
      setReplacement(null);
      setSelectError(null);
      return;
    }

    const myRequestId = ++selectRequestIdRef.current;

    setSelectedSecid(secid);
    setMaterialized(null);
    setReplacement(null);
    setSelectError(null);
    setIsSelecting(true);
    try {
      const materializeResult = await postMaterialize(secid);
      if (selectRequestIdRef.current !== myRequestId) return;
      setMaterialized(materializeResult);

      const replacementResult = await postReplacement({
        holdPositionId,
        targetInstrumentId: materializeResult.instrumentId,
        // Горизонт — настройка пользователя (Settings), не константа; не загружена — бэкенд подставит её сам.
        horizonYears: getReplacementHorizonYears(),
      });
      if (selectRequestIdRef.current !== myRequestId) return;
      setReplacement(replacementResult);
    } catch (err) {
      if (selectRequestIdRef.current !== myRequestId) return;
      setSelectError(err instanceof Error ? err.message : 'Не удалось сравнить с рынком');
    } finally {
      if (selectRequestIdRef.current === myRequestId) setIsSelecting(false);
    }
  };

  // Окно ±ComparableDurationWindowYears вокруг дюрации заменяемой позиции;
  // кандидаты без дюрации ИЛИ позиция без дюрации (holdDurationYears===null, окно не определить) —
  // скрываются при включённом фильтре, честнее, чем показать их без критерия сравнения.
  // Надёжность фильтрует СЕРВЕР (задача 50) — здесь остаётся только клиентское тонкое окно дюрации.
  const filteredCandidates = candidates.filter(
    (c) =>
      !durationFilterEnabled ||
      (c.durationYears !== null &&
        holdDurationYears !== null &&
        Math.abs(c.durationYears - holdDurationYears) <= ComparableDurationWindowYears),
  );
  const isReliabilityFilterActive = reliabilityFilter !== 'all';
  // Откат "bucket" → "all" на бэкенде (у позиции нет дюрации): честно говорим, что «похожий срок» не применён.
  const scopeFellBack = mode === 'market' && durationScope === 'bucket' && responseMeta.durationScope === 'all';
  const isBucketScopeApplied = mode === 'market' && responseMeta.durationScope === 'bucket';

  /** Карточка выгоды выбранного кандидата — рендерится под строкой ниже. */
  const renderBenefitCard = () => (
    <div style={{ marginTop: 8 }}>
      {isSelecting && (
        <Center py="sm">
          <Loader size="sm" />
        </Center>
      )}

      {!isSelecting && selectError && (
        <Alert color="red" data-testid={`replace-select-error-${holdPositionId}`}>
          {selectError}
        </Alert>
      )}

      {!isSelecting && !selectError && materialized && replacement && (
        <Paper ref={resultRef} withBorder p="sm" radius="md" data-testid={`replace-result-${holdPositionId}`}>
          <Text fw={600} size="sm">
            {materialized.metrics.name ?? materialized.metrics.issuer ?? materialized.secid}
          </Text>
          <Text size="sm" c="teal" fw={600} data-testid={`replace-benefit-${holdPositionId}`}>
            выгода{replacement.netBenefitAfterTaxRub !== null ? ' после налога' : ''} ≈{' '}
            {formatRub(replacement.netBenefitAfterTaxRub ?? replacement.netBenefitRub)}
            {replacement.annualizedBenefitFraction !== null && (
              <> (~{formatPercent(replacement.annualizedBenefitFraction)} годовых)</>
            )}{' '}
            за {formatHorizon(replacement.effectiveHorizonYears)}
          </Text>
          <Text size="xs" c="dimmed" data-testid={`replace-horizon-${holdPositionId}`}>
            {horizonCaption(toBreakdownData(replacement))}
          </Text>

          {replacement.targetRiskSignals && (
            <RiskSignalBadges signals={replacement.targetRiskSignals} testIdSuffix={`replace-${holdPositionId}`} />
          )}

          <ReplacementBreakdown data={toBreakdownData(replacement)} testIdSuffix={`candidate-${holdPositionId}`} />
        </Paper>
      )}
    </div>
  );

  return (
    <Stack gap="sm" data-testid={`replace-panel-${holdPositionId}`}>
      <SegmentedControl
        value={mode}
        onChange={handleModeChange}
        data={MODE_OPTIONS}
        data-testid={`replace-mode-${holdPositionId}`}
        fullWidth
      />

      {mode === 'search' ? (
        <MarketComparator holdPositionId={holdPositionId} />
      ) : (
        <>
          {!loadError && (
            <Stack gap={6}>
              {mode === 'market' && (
                <>
                  <Group gap={6} align="center" wrap="nowrap">
                    <Text size="xs" c="dimmed">
                      Срок:
                    </Text>
                    <SegmentedControl
                      size="xs"
                      value={durationScope}
                      onChange={(v) => handleDurationScopeChange(v as DurationScope)}
                      data={DURATION_SCOPE_OPTIONS}
                      data-testid={`replace-duration-scope-${holdPositionId}`}
                    />
                  </Group>
                  {isBucketScopeApplied && (
                    <Text size="xs" c="dimmed" data-testid={`replace-duration-scope-caption-${holdPositionId}`}>
                      {responseMeta.positionDurationBucket
                        ? `Бумаги из бакета дюрации позиции (${responseMeta.positionDurationBucket}) и соседних.`
                        : 'Бумаги из бакета дюрации позиции и соседних.'}
                    </Text>
                  )}
                  {scopeFellBack && (
                    <Text size="xs" c="dimmed" data-testid={`replace-duration-scope-fallback-${holdPositionId}`}>
                      Дюрация позиции неизвестна — «похожий срок» не применён, показан весь рынок.
                    </Text>
                  )}
                </>
              )}
              <ReliabilityFilterControl
                value={reliabilityFilter}
                onChange={handleReliabilityChange}
                testId={`replace-reliability-filter-${holdPositionId}`}
              />
              {mode === 'market' && (
                <Text size="xs" c="dimmed" data-testid={`replace-ranking-caption-${holdPositionId}`}>
                  Кандидаты ранжированы по доходности к худшему из погашения и оферты после налога
                  {responseMeta.taxRateFraction !== null && <> (НДФЛ {formatPercent(responseMeta.taxRateFraction)} учтён)</>}, а не
                  по сырой YTM.
                </Text>
              )}
            </Stack>
          )}

          {isLoading && (
            <Center py="sm">
              <Loader size="sm" />
            </Center>
          )}

          {!isLoading && loadError && (
            <Alert color="red" data-testid={`replace-candidates-error-${holdPositionId}`}>
              {loadError}
            </Alert>
          )}

          {!isLoading && !loadError && candidates.length === 0 && isReliabilityFilterActive && (
            <Stack gap={4} data-testid={`replace-reliability-empty-${holdPositionId}`}>
              <Text size="xs" c="dimmed">
                {isBucketScopeApplied
                  ? 'Нет кандидатов с выбранной надёжностью в этом бакете — расширьте до всего рынка.'
                  : 'Нет кандидатов с выбранной надёжностью — выберите «все» или смягчите уровень.'}
              </Text>
              {isBucketScopeApplied && (
                <Button
                  size="xs"
                  variant="light"
                  w="fit-content"
                  onClick={() => handleDurationScopeChange('all')}
                  data-testid={`replace-expand-scope-${holdPositionId}`}
                >
                  Весь рынок
                </Button>
              )}
            </Stack>
          )}

          {!isLoading && !loadError && candidates.length === 0 && !isReliabilityFilterActive && (
            <Text size="xs" c="dimmed" data-testid={`replace-candidates-empty-${holdPositionId}`}>
              {mode === 'rv'
                ? 'Нет данных для сравнения по корзине этой позиции (сектор/дюрация/спред не определились).'
                : isBucketScopeApplied
                  ? 'Кандидатов в этом бакете не найдено — расширьте до всего рынка.'
                  : 'Кандидатов не найдено.'}
            </Text>
          )}

          {!isLoading && !loadError && candidates.length > 0 && (
            <>
              <Group justify="space-between" wrap="wrap" gap="xs">
                <Switch
                  label={`Похожая дюрация (±${formatNumber(ComparableDurationWindowYears, 1)} года)`}
                  checked={durationFilterEnabled}
                  onChange={(e) => setDurationFilterEnabled(e.currentTarget.checked)}
                  data-testid={`replace-duration-filter-${holdPositionId}`}
                />
                {durationFilterEnabled && (
                  <Text size="xs" c="dimmed" data-testid={`replace-duration-filter-count-${holdPositionId}`}>
                    осталось {filteredCandidates.length} из {candidates.length}
                  </Text>
                )}
              </Group>

              {durationFilterEnabled && filteredCandidates.length === 0 ? (
                <Text size="xs" c="dimmed" data-testid={`replace-duration-filter-empty-${holdPositionId}`}>
                  Нет кандидатов по текущим фильтрам — отключите фильтр.
                </Text>
              ) : (
                <Stack gap="xs" data-testid={`replace-candidates-list-${holdPositionId}`}>
                  <RiskSignalsCaption />
                  {filteredCandidates.map((c) => (
                    <div key={c.secid}>
                      <ReplacementCandidateRow
                        candidate={c}
                        taxRateFraction={responseMeta.taxRateFraction}
                        onSelect={handleSelectCandidate}
                        isSelected={selectedSecid === c.secid}
                        isLoading={isSelecting && selectedSecid === c.secid}
                      />
                      <Collapse expanded={selectedSecid === c.secid} data-testid={`replace-benefit-collapse-${c.secid}`}>
                        {selectedSecid === c.secid && renderBenefitCard()}
                      </Collapse>
                    </div>
                  ))}
                </Stack>
              )}
            </>
          )}
        </>
      )}
    </Stack>
  );
}

/**
 * Карточка одной слабой позиции (было `SellCandidateCard`) — бейджи-причины
 * (оценочные, «кандидат», не «продайте»), RV-бейдж (отказ GET /relative-value не роняет
 * карточку — undefined просто не рендерится), раскрывашка «подобрать замену» → <see>ReplacementPanel</see>.
 */
function WeakPositionCard({
  row,
  reasons,
  initiallyExpanded = false,
}: {
  row: ComparisonRow;
  reasons: { kind: string; label: string }[];
  /** Задача 49: переход «подобрать замену» из вердикта позиции (`?positionId=`) сразу раскрывает панель подбора. */
  initiallyExpanded?: boolean;
}) {
  const [expanded, setExpanded] = useState(initiallyExpanded);
  const rv = useRelativeValueStore((s) => s.positionsById[row.positionId]);

  return (
    <Paper withBorder p="md" radius="md" data-testid={`sell-candidate-${row.positionId}`}>
      <Group justify="space-between" mb="xs">
        <Text fw={600}>{row.name ?? row.issuer ?? `Позиция #${row.positionId}`}</Text>
        <Text size="sm" c="dimmed">
          {formatPercent(row.effectiveYield)}
        </Text>
      </Group>
      <Group gap={6} wrap="wrap" mb="xs">
        {reasons.map((reason) => (
          <Badge key={reason.kind} size="sm" color="orange" variant="light">
            {reason.label}
          </Badge>
        ))}
        {reasons.length === 0 && (
          <Badge size="sm" color="gray" variant="light">
            кандидат на сравнение
          </Badge>
        )}
        {rv && <RelativeValueBadge rv={rv} />}
      </Group>

      <UnstyledButton onClick={() => setExpanded((v) => !v)} data-testid={`replace-toggle-${row.positionId}`}>
        <Text size="xs" fw={600} c="blue">
          {expanded ? 'скрыть подбор замены' : 'подобрать замену'}
        </Text>
      </UnstyledButton>
      <Collapse expanded={expanded}>
        <div style={{ marginTop: 8 }}>
          {expanded && <ReplacementPanel holdPositionId={row.positionId} holdDurationYears={row.macaulayDurationYears} />}
        </div>
      </Collapse>
    </Paper>
  );
}

/**
 * Блок 1 «Слабые позиции → подобрать замену» — объединяет прежние «слабые звенья» +
 * «матрица замен» + «RV-секцию дешёвых соседей» в один сценарий: список самых слабых по доходности
 * позиций, у каждой — раскрывашка режимов подбора замены (см. <see>ReplacementPanel</see>).
 */
function WeakLinksSection() {
  const { sellCandidates, outOfComparison, comparisonRows, comparisonDisclaimer, isLoading, error } = useRecommendationsStore();

  // Задача 49: `?positionId=N` (ссылка «подобрать замену» из вердикта позиции) — выбранная позиция раскрыта сразу;
  // если она не среди «слабых» (топ-3 по доходности), показываем её отдельной карточкой над списком.
  const [searchParams] = useSearchParams();
  const selectedId = Number(searchParams.get('positionId')) || null;
  const selectedRow = selectedId === null ? undefined : comparisonRows.find((r) => r.positionId === selectedId);
  const pinnedRow = selectedRow && !sellCandidates.some((c) => c.row.positionId === selectedRow.positionId) ? selectedRow : null;

  return (
    <Paper withBorder p="md" radius="md" data-testid="weak-links-section">
      <Title order={4} mb="sm">
        Слабые позиции — подобрать замену
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Оценка по доходности относительно медианы портфеля, горизонта и концентрации по эмитенту —
        это аналитическая подборка, не инвестиционная рекомендация продавать что-либо.
      </Text>

      {isLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" data-testid="weak-links-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && sellCandidates.length === 0 && outOfComparison.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="weak-links-empty">
          Данные появятся после синхронизации с брокерским счётом.
        </Text>
      )}

      {!isLoading && !error && pinnedRow && (
        <Stack gap="sm" mb="sm" data-testid="selected-position-section">
          <Text size="xs" fw={600} c="dimmed">
            Выбранная позиция
          </Text>
          <WeakPositionCard row={pinnedRow} reasons={[]} initiallyExpanded />
        </Stack>
      )}

      {!isLoading && !error && sellCandidates.length > 0 && (
        <Stack gap="sm">
          {sellCandidates.map((c) => (
            <WeakPositionCard
              key={c.row.positionId}
              row={c.row}
              reasons={c.reasons}
              initiallyExpanded={c.row.positionId === selectedId}
            />
          ))}
        </Stack>
      )}

      {!isLoading && !error && outOfComparison.length > 0 && (
        <Stack gap="xs" mt="md">
          <Text size="xs" fw={600} c="dimmed">
            Вне сравнения (флоатеры/индексируемые/неполные данные — доходность несравнима с YTM)
          </Text>
          {outOfComparison.map((row) => (
            <OutOfComparisonRow key={row.positionId} row={row} />
          ))}
        </Stack>
      )}

      <Disclaimer text={comparisonDisclaimer} />
    </Paper>
  );
}

/** Секция «Watchlist» — ручной список бумаг вне текущих позиций: ввод ISIN + заметка, таблица метрик, удаление. */
function WatchlistSection() {
  const { items, disclaimer, isLoading, error, isAdding, addError, load, add, remove, clearAddError } =
    useWatchlistStore();
  const [isin, setIsin] = useState('');
  const [note, setNote] = useState('');

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleAdd = async () => {
    if (!isin.trim()) return;
    const ok = await add(isin.trim(), note.trim() || undefined);
    if (ok) {
      setIsin('');
      setNote('');
    }
  };

  return (
    <Paper withBorder p="md" radius="md" data-testid="watchlist-section">
      <Title order={4} mb="sm">
        Watchlist — бумаги вне портфеля
      </Title>
      <Text size="xs" c="dimmed" mb="sm">
        Ручной список ISIN для сравнения с портфелем в тех же координатах (YTM, дюрация, G-спред) —
        не скринер по всей бирже.
      </Text>

      <Group align="flex-end" mb="sm" wrap="wrap">
        <TextInput
          label="ISIN"
          placeholder="RU000A1038V6"
          value={isin}
          onChange={(e) => {
            setIsin(e.currentTarget.value);
            if (addError) clearAddError();
          }}
          data-testid="watchlist-isin-input"
          w={220}
        />
        <TextInput
          label="Заметка (необязательно)"
          value={note}
          onChange={(e) => setNote(e.currentTarget.value)}
          data-testid="watchlist-note-input"
          w={260}
        />
        <Button onClick={handleAdd} loading={isAdding} data-testid="watchlist-add-button">
          Добавить
        </Button>
      </Group>

      {addError && (
        <Alert color="red" mb="sm" data-testid="watchlist-add-error">
          {addError}
        </Alert>
      )}

      {isLoading && (
        <Center py="md">
          <Loader size="sm" />
        </Center>
      )}

      {!isLoading && error && (
        <Alert color="red" data-testid="watchlist-error">
          {error}
        </Alert>
      )}

      {!isLoading && !error && items.length === 0 && (
        <Text size="sm" c="dimmed" data-testid="watchlist-empty">
          Пока пусто — добавьте ISIN бумаги, за которой хотите наблюдать.
        </Text>
      )}

      {!isLoading && !error && items.length > 0 && (
        <Table.ScrollContainer minWidth={700}>
          <Table striped highlightOnHover data-testid="watchlist-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Бумага</Table.Th>
                <Table.Th>Доходность</Table.Th>
                <Table.Th>Дюрация, лет</Table.Th>
                <Table.Th>G-спред, б.п.</Table.Th>
                <Table.Th>Добавлена</Table.Th>
                <Table.Th>Заметка</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((item) => (
                <Table.Tr key={item.id} data-testid={`watchlist-row-${item.id}`}>
                  <Table.Td>
                    {item.name ?? item.issuer ?? item.isin}
                    {item.dataIncomplete && (
                      <Badge ml={6} size="xs" color="gray" variant="outline">
                        неполные данные
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{formatPercent(item.effectiveYield)}</Table.Td>
                  <Table.Td>{item.modifiedDuration != null ? formatNumber(item.modifiedDuration, 2) : '—'}</Table.Td>
                  <Table.Td>{item.gSpread != null ? formatNumber(item.gSpread, 0) : '—'}</Table.Td>
                  <Table.Td>{formatDate(item.addedAtUtc)}</Table.Td>
                  <Table.Td>{item.note ?? '—'}</Table.Td>
                  <Table.Td>
                    <ActionIcon
                      color="red"
                      variant="subtle"
                      onClick={() => remove(item.id)}
                      data-testid={`watchlist-remove-${item.id}`}
                      aria-label="Удалить из watchlist"
                    >
                      ×
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {!isLoading && !error && items.length > 0 && <Disclaimer text={disclaimer} />}
    </Paper>
  );
}

/**
 * Страница «Рекомендации» (задача 35 — переработка в два главных блока, один сценарий =
 * один блок): Блок 1 «Слабые позиции → подобрать замену» (<see>WeakLinksSection</see>, поглощает
 * прежние «слабые звенья»+«матрица замен»+«RV дешёвые соседи») и Блок 2 «Куда вложить сумму»
 * (<see>BasketConstructor</see>, задача 35 добавила переключатель источника кандидатов). Watchlist —
 * вспомогательная секция внизу, независимый стор/загрузка. Дисклеймер сверху страницы +
 * дисклеймер каждого блока (текст бэкенда). Все формулировки — оценочные, не индивидуальные
 * инвестрекомендации.
 */
export function Recommendations() {
  const load = useRecommendationsStore((s) => s.load);
  const loadRelativeValue = useRelativeValueStore((s) => s.load);

  useEffect(() => {
    load();
    // RV грузится ОТДЕЛЬНЫМ вызовом (не Promise.all внутри useRecommendationsStore.load) —
    // отказ GET /api/analytics/relative-value не должен блокировать/ронять остальную страницу.
    loadRelativeValue();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Page
      title="Рекомендации"
      description="Кандидаты на замену, корзина и watchlist. Все оценки — аналитические, основаны на текущих данных портфеля, не индивидуальная инвестиционная рекомендация."
      width="wide"
    >
      <div data-testid="page-disclaimer">
        <Disclaimer />
      </div>

      <WeakLinksSection />
      <BasketConstructor />
      <WatchlistSection />
    </Page>
  );
}
