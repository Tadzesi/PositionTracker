import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import App from './App';
import { SessionProvider } from './components/Layout';
import { I18nProvider } from './lib/i18n';

const NO_SESSION = Symbol('no session');

function mockFetch(routes: Record<string, { status?: number; body?: unknown }>) {
  const calls: { url: string; method: string; headers: Record<string, string>; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method ?? 'GET', headers: (init?.headers ?? {}) as Record<string, string>, body: init?.body as string | undefined });
    const hit = routes[`${init?.method ?? 'GET'} ${url}`];
    if (!hit || hit.body === NO_SESSION) return new Response(JSON.stringify({ code: 'unauthorized', message: { sk: 'Neprihlásený', en: 'Not signed in' } }), { status: 401 });
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
    const routes: Record<string, { status?: number; body?: unknown }> = { 'POST /api/account/signin/redeem': { body: {} }, 'GET /api/account/session': { body: NO_SESSION }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } };
    const calls = mockFetch(routes);
    // The session cookie appears with the redemption.
    const inner = globalThis.fetch;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url === '/api/account/signin/redeem') routes['GET /api/account/session'] = { body: { name: 'A', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' } };
      return inner(url, init);
    }));
    renderAt('/account/signin?token=abc');
    expect(calls.some((c) => c.url.includes('redeem'))).toBe(false);
    await userEvent.click(await screen.findByRole('button', { name: 'Continue to my events' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/account/signin/redeem' && c.body === JSON.stringify({ token: 'abc' }))).toBe(true));
    expect(await screen.findByText('My events', { selector: 'h1' })).toBeInTheDocument();
  });

  it('a signed-in organizer is sent from the landing and sign-in pages straight to the events', async () => {
    const me = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
    mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } });
    const first = renderAt('/');
    expect(await screen.findByText('My events', { selector: 'h1' })).toBeInTheDocument();
    first.unmount();
    renderAt('/account/signin');
    expect(await screen.findByText('My events', { selector: 'h1' })).toBeInTheDocument();
  });

  it('an expired sign-in link offers to send a new one', async () => {
    mockFetch({ 'POST /api/account/signin/redeem': { status: 400, body: { code: 'link_expired', message: { sk: 'Platnosť vypršala', en: 'This link has expired' } } } });
    renderAt('/account/signin?token=old');
    await userEvent.click(await screen.findByRole('button', { name: 'Continue to my events' }));
    expect(await screen.findByText('This link has expired')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send a sign-in link' })).toBeInTheDocument();
  });

  it('an empty event list asks for the first event; a live event links to race control', async () => {
    const me = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
    mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } });
    const first = renderAt('/org');
    expect(await screen.findByText('Create your first event')).toBeInTheDocument();
    first.unmount();
    mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [{ eventId: 'e1', name: 'Derby', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'live', laps: 2, lapLengthM: 12400, participants: { invited: 3, confirmed: 9, declined: 0, emailFailed: 0 } }] } } });
    renderAt('/org');
    expect(await screen.findByRole('link', { name: 'Race control' })).toHaveAttribute('href', '/org/events/e1/control');
    expect(screen.getByRole('button', { name: '+ New event' })).toBeInTheDocument();
  });

  it('the events list shows laps, length and how many of the invited have confirmed', async () => {
    const me = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
    mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [
      { eventId: 'e1', name: 'Derby', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'ready', laps: 2, lapLengthM: 12400, participants: { invited: 3, confirmed: 9, declined: 1, emailFailed: 0 } },
      { eventId: 'e2', name: 'Prázdne', location: 'Martin', startTime: '2026-11-09T10:00:00Z', sport: 'running', state: 'draft', laps: 1, lapLengthM: null, participants: { invited: 0, confirmed: 0, declined: 0, emailFailed: 0 } }] } } });
    renderAt('/org');
    expect(await screen.findByText('Cycling · 2 × 12.4 km')).toBeInTheDocument();
    expect(screen.getByText('9')).toBeInTheDocument();
    expect(screen.getByText(/\/ 13 confirmed/)).toBeInTheDocument();
    expect(screen.getByText('3 waiting')).toBeInTheDocument();
    expect(screen.getByText('Nobody invited')).toBeInTheDocument();
    expect(screen.getByText('Running · no course')).toBeInTheDocument();
  });

  const ME = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
  const manage = (over: Record<string, unknown> = {}) => ({
    eventId: 'e1', name: 'Derby', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'draft', laps: 1, spectatorDelaySeconds: 30,
    course: { lapLengthM: 12400, ascentM: 310, descentM: 305, pointCount: 4, closed: true, uploadedAt: '2026-10-30T10:00:00Z', checkpoints: [{ name: 'Bufet', lapDistanceM: 3000 }] },
    categories: [], waves: [], participants: { invited: 3, confirmed: 9, declined: 1, emailFailed: 2 }, running: false, emailMode: 'smtp',
    createdAt: '2026-10-30T10:00:00Z', endedAt: null, confirmationDeadline: null, ...over,
  });
  const PREVIEW = {
    lapLengthM: 12400, laps: 1, closed: true, hasElevation: true, minElevationM: 290, maxElevationM: 340, ascentM: 310, descentM: 305,
    points: [[49, 18, 300, 0], [49.01, 18, 340, 4000], [49.02, 18, 290, 8000], [49.03, 18, 300, 12400]], checkpoints: [{ name: 'Bufet', lapDistanceM: 6200, lat: 49.015, lon: 18 }],
  };

  it('the event page shows the participant counts, the next step and the course numbers with the elevation profile', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW } });
    renderAt('/org/events/e1');
    expect(await screen.findByText('What next')).toBeInTheDocument();
    expect(screen.getByText('Invited in total').previousSibling).toHaveTextContent('15');
    expect(screen.getByText('Confirmed', { selector: 'span' }).previousSibling).toHaveTextContent('9');
    expect(screen.getByText('Email failed')).toBeInTheDocument();
    expect(screen.getByText('Waiting').closest('a')).toHaveAttribute('href', '/org/events/e1/participants?status=Invited');
    expect(await screen.findByRole('img', { name: 'Elevation profile of one lap' })).toBeInTheDocument();
    expect(screen.getByText('290–340 m')).toBeInTheDocument();
  });

  it('a course without elevation says why there is no profile and no ascent', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() },
      'GET /api/events/e1/course-preview': { body: { ...PREVIEW, hasElevation: false, minElevationM: null, maxElevationM: null, points: PREVIEW.points.map((p) => [p[0], p[1], null, p[3]]) } } });
    renderAt('/org/events/e1');
    expect(await screen.findByText(/no elevation data/)).toBeInTheDocument();
    expect(screen.queryByRole('img', { name: 'Elevation profile of one lap' })).toBeNull();
  });

  it('edits the event and shows the server message when the new start does not fit the waves', async () => {
    const calls = mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'PUT /api/events/e1': { status: 409, body: { code: 'start_conflict', field: 'startTime', message: { sk: 'Vlny nesedia', en: 'The waves do not fit' } } } });
    renderAt('/org/events/e1');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const name = screen.getByLabelText('Name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Derby 2026');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('The waves do not fit')).toBeInTheDocument();
    const put = calls.find((c) => c.method === 'PUT' && c.url === '/api/events/e1')!;
    expect(JSON.parse(put.body!)).toMatchObject({ name: 'Derby 2026', location: 'Žilina', sport: 'cycling' });
  });

  it('checkpoints are entered in km with a visible unit, validated, and saved without uploading the GPX again', async () => {
    const calls = mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'PUT /api/events/e1/course': { body: manage({ course: { ...manage().course, checkpoints: [{ name: 'Bufet', lapDistanceM: 3000 }, { name: 'Kopec', lapDistanceM: 9000 }] } }) } });
    renderAt('/org/events/e1');
    await screen.findByText('Checkpoints');
    expect(screen.getByLabelText('Distance from the lap start (km)')).toHaveValue('3');
    expect(screen.getAllByText('km from start').length).toBe(1);
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Add a point at half of the lap' }));
    const km = screen.getAllByLabelText('Distance from the lap start (km)');
    expect(km).toHaveLength(2);
    await userEvent.clear(km[1]);
    await userEvent.type(km[1], '99');
    expect(await screen.findByText(/must lie inside the lap/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();

    await userEvent.clear(km[1]);
    await userEvent.type(km[1], '9,0');
    const names = screen.getAllByLabelText('Checkpoint name');
    await userEvent.clear(names[1]);
    await userEvent.type(names[1], 'Kopec');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/events/e1/course')).toBe(true));
    const body = JSON.parse(calls.find((c) => c.method === 'PUT' && c.url === '/api/events/e1/course')!.body!);
    expect(body.gpxBase64).toBeUndefined();
    expect(body.checkpoints).toEqual([{ name: 'Bufet', lapDistanceM: 3000 }, { name: 'Kopec', lapDistanceM: 9000 }]);
  });

  it('a checkpoint can be removed one by one or all at once', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW } });
    renderAt('/org/events/e1');
    await screen.findByText('Checkpoints');
    await userEvent.click(screen.getByRole('button', { name: 'Add a point at half of the lap' }));
    expect(screen.getAllByLabelText('Checkpoint name')).toHaveLength(2);
    await userEvent.click(screen.getAllByRole('button', { name: 'Remove checkpoint' })[1]);
    expect(screen.getAllByLabelText('Checkpoint name')).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Add a point at half of the lap' }));
    await userEvent.click(screen.getByRole('button', { name: /Remove all points \(2\)/ }));
    expect(screen.queryAllByLabelText('Checkpoint name')).toHaveLength(0);
    expect(screen.getByText('No checkpoints yet.')).toBeInTheDocument();
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
