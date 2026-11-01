export interface LocalizedText { sk: string; en: string }

/** The path the app is served under ("" at the root, "/tracker/app" when deployed under a sub-path: build with VITE_BASE). */
export const BASE = import.meta.env.BASE_URL.replace(/\/$/, '');

export class ApiError extends Error {
  constructor(public status: number, public code: string, public text: LocalizedText, public field?: string) {
    super(text.en);
  }
}

interface EarlyRequest { t: number; p: Promise<Response> }

/** public/boot.js starts the first requests of a spectator link before the bundle is loaded; the first GET of that path takes the response over. */
function takeEarly(path: string): Promise<Response> | undefined {
  const store = (window as unknown as { __ptEarly?: Record<string, EarlyRequest> }).__ptEarly;
  const e = store?.[path];
  if (!store || !e) return undefined;
  delete store[path];
  return Date.now() - e.t < 15000 ? e.p : undefined;
}

let actAs: string | null = null;
/** An administrator working on an organizer's event: every organizer request then carries the organizer's id (not the account and admin calls). */
export function setActAs(organizerId: string | null) { actAs = organizerId; }

/** JSON API wrapper: sends the browser headers every unsafe request needs and turns the error shape into ApiError. */
export async function api<T = void>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  const init: RequestInit = { method, credentials: 'same-origin', headers };
  if (actAs && !path.startsWith('/api/account/') && !path.startsWith('/api/admin/')) headers['X-Act-As-Organizer'] = actAs;
  if (method !== 'GET') {
    headers['X-Requested-With'] = 'PositionTracker';
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body ?? {});
  }
  let response: Response;
  try {
    const early = method === 'GET' ? takeEarly(path) : undefined;
    response = await (early ? early.catch(() => fetch(BASE + path, init)) : fetch(BASE + path, init));
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

/** A file from the API (the CSV export): the same cookie and administrator header as the JSON calls. */
export async function download(path: string): Promise<Blob> {
  const headers: Record<string, string> = {};
  if (actAs) headers['X-Act-As-Organizer'] = actAs;
  let response: Response;
  try {
    response = await fetch(BASE + path, { credentials: 'same-origin', headers });
  } catch {
    throw new ApiError(0, 'network', { sk: 'Server je nedostupný. Skontrolujte pripojenie.', en: 'The server cannot be reached. Check your connection.' });
  }
  if (!response.ok) throw new ApiError(response.status, 'error', { sk: `Chyba ${response.status}.`, en: `Error ${response.status}.` });
  return response.blob();
}

export const get = <T,>(path: string) => api<T>('GET', path);
export const post = <T = void,>(path: string, body?: unknown) => api<T>('POST', path, body);
export const put = <T = void,>(path: string, body?: unknown) => api<T>('PUT', path, body);
export const del = <T = void,>(path: string) => api<T>('DELETE', path);
