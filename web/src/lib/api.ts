export interface LocalizedText { sk: string; en: string }

/** The path the app is served under ("" at the root, "/tracker/app" when deployed under a sub-path: build with VITE_BASE). */
export const BASE = import.meta.env.BASE_URL.replace(/\/$/, '');

export class ApiError extends Error {
  constructor(public status: number, public code: string, public text: LocalizedText, public field?: string) {
    super(text.en);
  }
}

/** JSON API wrapper: sends the browser headers every unsafe request needs and turns the error shape into ApiError. */
export async function api<T = void>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  const init: RequestInit = { method, credentials: 'same-origin', headers };
  if (method !== 'GET') {
    headers['X-Requested-With'] = 'PositionTracker';
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body ?? {});
  }
  let response: Response;
  try {
    response = await fetch(BASE + path, init);
  } catch {
    throw new ApiError(0, 'network', { sk: 'Server je nedostupný. Skontrolujte pripojenie.', en: 'The server cannot be reached. Check your connection.' });
  }
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  let json: unknown = undefined;
  try { json = text ? JSON.parse(text) : undefined; } catch { /* not JSON */ }
  if (!response.ok) {
    const e = json as { code?: string; message?: LocalizedText; field?: string } | undefined;
    throw new ApiError(response.status, e?.code ?? 'error',
      e?.message ?? { sk: `Chyba ${response.status}.`, en: `Error ${response.status}.` }, e?.field);
  }
  return json as T;
}

export const get = <T,>(path: string) => api<T>('GET', path);
export const post = <T = void,>(path: string, body?: unknown) => api<T>('POST', path, body);
export const put = <T = void,>(path: string, body?: unknown) => api<T>('PUT', path, body);
export const del = <T = void,>(path: string) => api<T>('DELETE', path);
