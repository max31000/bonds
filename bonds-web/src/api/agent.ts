import { apiClient } from './client';
import type { AgentSettingsRequest, AgentState } from './types';

/** GET /api/agent — настройки ИИ-агента, доступность и история запусков (задача 68). */
export function fetchAgentState(): Promise<AgentState> {
  return apiClient.get<AgentState>('/agent');
}

/** PUT /api/agent/settings — 422: включение без подключения Claude Code, неверная модель или лимит. */
export function saveAgentSettings(request: AgentSettingsRequest): Promise<AgentState> {
  return apiClient.put<AgentState>('/agent/settings', request);
}
