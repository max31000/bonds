import { create } from 'zustand';
import { approvePlan, fetchPlans, rejectPlan } from '../api/plans';
import { cancelPlanExecution, setExecutionPaused } from '../api/execution';
import type { Plan, PlansResponse } from '../api/types';

/** Результат решения по плану: ошибка несёт текст бэкенда (409: план уже решён или просрочен). */
export type PlanDecisionResult = { ok: true } | { ok: false; error: string };

interface PlansStore {
  data: PlansResponse | null;
  isLoading: boolean;
  error: string | null;
  /** id плана, по которому идёт запрос решения (кнопки блокируются). */
  decidingId: number | null;
  load: () => Promise<void>;
  approve: (id: number) => Promise<PlanDecisionResult>;
  reject: (id: number) => Promise<PlanDecisionResult>;
  /** Снять висящие заявки исполняемого плана (задача 65). */
  cancelExecution: (id: number) => Promise<PlanDecisionResult>;
  /** «Остановить» = пауза (/stop): исполнитель снимает висящие заявки и не отправляет новые; список перечитывается. */
  stop: () => Promise<void>;
}

let loadSeq = 0;

function replacePlan(data: PlansResponse | null, plan: Plan): PlansResponse | null {
  if (!data) return data;
  return { ...data, items: data.items.map((p) => (p.id === plan.id ? plan : p)) };
}

/**
 * Планы сделок (задача 63): серверные данные, без persist. РЕЖИМ НАБЛЮДЕНИЯ — решения по плану ничего не отправляют брокеру.
 * Устаревший ответ загрузки отбрасывается по номеру запроса.
 */
export const usePlansStore = create<PlansStore>()((set, get) => ({
  data: null,
  isLoading: false,
  error: null,
  decidingId: null,
  load: async () => {
    const seq = ++loadSeq;
    set({ isLoading: true, error: null });
    try {
      const data = await fetchPlans();
      if (seq !== loadSeq) return;
      set({ data, isLoading: false });
    } catch (err) {
      if (seq !== loadSeq) return;
      set({ error: err instanceof Error ? err.message : 'Не удалось загрузить планы', isLoading: false });
    }
  },
  approve: (id) => decide(id, approvePlan, 'Не удалось подтвердить план', set, get),
  reject: (id) => decide(id, rejectPlan, 'Не удалось отклонить план', set, get),
  cancelExecution: (id) => decide(id, cancelPlanExecution, 'Не удалось запросить отмену заявок', set, get),
  stop: async () => {
    await setExecutionPaused(true);
    await get().load();
  },
}));

async function decide(
  id: number,
  action: (id: number) => Promise<Plan>,
  fallbackError: string,
  set: (partial: Partial<PlansStore>) => void,
  get: () => PlansStore,
): Promise<PlanDecisionResult> {
  set({ decidingId: id });
  try {
    const plan = await action(id);
    ++loadSeq;
    set({ data: replacePlan(get().data, plan), decidingId: null });
    return { ok: true };
  } catch (err) {
    set({ decidingId: null });
    // План мог быть решён/просрочен в другом месте — обновим список, чтобы статус в UI был актуальным.
    void get().load();
    return { ok: false, error: err instanceof Error ? err.message : fallbackError };
  }
}
