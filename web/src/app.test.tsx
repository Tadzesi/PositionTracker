import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import App from './App';
import { SessionProvider } from './components/Layout';
import { I18nProvider } from './lib/i18n';

function mockFetch(routes: Record<string, { status?: number; body?: unknown }>) {
  const calls: { url: string; method: string; headers: Record<string, string>; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method ?? 'GET', headers: (init?.headers ?? {}) as Record<string, string>, body: init?.body as string | undefined });
    const hit = routes[`${init?.method ?? 'GET'} ${url}`];
    if (!hit) return new Response(JSON.stringify({ code: 'unauthorized', message: { sk: 'Neprihlásený', en: 'Not signed in' } }), { status: 401 });
    return new Response(hit.status === 204 ? null : JSON.stringify(hit.body ?? {}), { status: hit.status ?? 200 });
  }));
  return calls;
}

const renderAt = (path: string, lang: 'sk' | 'en' = 'en') => render(
  <I18nProvider initial={lang}><SessionProvider><MemoryRouter initialEntries={[path]}><App /></MemoryRouter></SessionProvider></I18nProvider>);

afterEach(() => vi.unstubAllGlobals());

describe('web app', () => {
  it('shows the landing page in the chosen language with sign-up and sign-in', async () => {
    mockFetch({});
    renderAt('/', 'sk');
    expect(await screen.findByText('Sledovanie pretekov naživo')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Vytvoriť účet organizátora' })).toBeInTheDocument();
  });

  it('sign-up posts with the browser headers and shows the check-your-email page', async () => {
    const calls = mockFetch({ 'POST /api/account/signup': { status: 202, body: { status: 'accepted' } } });
    renderAt('/account/signup');
    await userEvent.type(await screen.findByLabelText('Name'), 'Anna');
    await userEvent.type(screen.getByLabelText('E-mail'), 'anna@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByText('Check your email')).toBeInTheDocument();
    const post = calls.find((c) => c.method === 'POST')!;
    expect(post.headers['X-Requested-With']).toBe('PositionTracker');
    expect(post.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(post.body!)).toMatchObject({ name: 'Anna', email: 'anna@example.org', language: 'en' });
  });

  it('shows a field error from the server next to the field', async () => {
    mockFetch({ 'POST /api/account/signup': { status: 400, body: { code: 'validation_failed', field: 'email', message: { sk: 'Neplatný e-mail', en: 'Invalid email' } } } });
    renderAt('/account/signup');
    await userEvent.type(await screen.findByLabelText('Name'), 'Anna');
    await userEvent.type(screen.getByLabelText('E-mail'), 'bad@x.org');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByText('Invalid email')).toBeInTheDocument();
  });

  it('sign-in link needs an explicit click and then posts the token', async () => {
    const calls = mockFetch({ 'POST /api/account/signin/redeem': { body: {} }, 'GET /api/account/session': { body: { name: 'A', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' } }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } });
    renderAt('/account/signin?token=abc');
    expect(calls.some((c) => c.url.includes('redeem'))).toBe(false);
    await userEvent.click(await screen.findByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/account/signin/redeem' && c.body === JSON.stringify({ token: 'abc' }))).toBe(true));
    expect(await screen.findByText('My events', { selector: 'h1' })).toBeInTheDocument();
  });

  it('the participant page shows the invitation state and a link problem for an expired link', async () => {
    mockFetch({ 'GET /api/p/good': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Invited', locked: { gender: false } }, language: 'en' } },
      'GET /api/p/old': { status: 400, body: { code: 'link_expired', message: { sk: 'Platnosť vypršala', en: 'This link has expired' } } } });
    const first = renderAt('/p?token=good');
    expect(await screen.findByText('Time trial')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Confirm participation' })).toBeInTheDocument();
    first.unmount();
    renderAt('/p?token=old');
    expect(await screen.findByText('This link has expired')).toBeInTheDocument();
  });
});
