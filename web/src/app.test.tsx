import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import App from './App';
import { SessionProvider } from './components/Layout';
import { I18nProvider } from './lib/i18n';
import { STOP_DEEP_LINK } from './owntracksStop';

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
  it('before the start the spectator page shows the event, the start time and a countdown, then switches to the map', async () => {
    const startTime = new Date(Date.now() + ((2 * 24 + 3) * 60 + 30) * 60000).toISOString();
    mockFetch({
      'GET /api/events/e1/course': { status: 404, body: { code: 'not_found' } },
      'GET /api/events/e1/public': { status: 404, body: { code: 'not_found' } },
      'GET /api/events/e1/info': { body: { state: 'ready', name: 'Time trial', location: 'Zilina', startTime, sport: 'cycling' } },
    });
    renderAt('/e/e1');
    expect(await screen.findByRole('heading', { name: 'Time trial' })).toBeInTheDocument();
    expect(screen.getByText(/Starts in 2 d 3 h/)).toBeInTheDocument();
    expect(screen.getByText(/Zilina/)).toBeInTheDocument();
  });

  it('shows the landing page in the chosen language with sign-up and sign-in', async () => {
    mockFetch({});
    renderAt('/', 'sk');
    expect(await screen.findByText('Sledovanie pretekov naživo')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Vytvoriť účet organizátora' })).toBeInTheDocument();
  });

  it('every page links the privacy notice from the footer; the notice lists the stored cookie and browser data', async () => {
    mockFetch({});
    renderAt('/', 'sk');
    await userEvent.click(await screen.findByRole('link', { name: 'Ochrana osobných údajov a cookies' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Ochrana osobných údajov a cookies' })).toBeInTheDocument();
    for (const name of ['__Secure-pt-session', 'pt-lang', 'pt-theme', 'pt-follow-…']) expect(screen.getByText(name)).toBeInTheDocument();
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

  it('the code from the sign-in mail is typed on the same page and opens the events without leaving the browser', async () => {
    const routes: Record<string, { status?: number; body?: unknown }> = { 'POST /api/account/signin': { status: 202, body: { status: 'accepted' } },
      'POST /api/account/code': { body: {} }, 'GET /api/account/session': { body: NO_SESSION }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } };
    const calls = mockFetch(routes);
    const inner = globalThis.fetch;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url === '/api/account/code') routes['GET /api/account/session'] = { body: { name: 'A', email: 'anna@example.org', language: 'en', emailConfirmed: true, emailMode: 'smtp' } };
      return inner(url, init);
    }));
    renderAt('/account/signin');
    await userEvent.type(await screen.findByLabelText('E-mail'), 'anna@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Send a sign-in code' }));
    const continueButton = await screen.findByRole('button', { name: 'Continue' });
    expect(continueButton).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Code from the email'), '123 45x6');
    await userEvent.click(continueButton);
    await waitFor(() => expect(calls.some((c) => c.url === '/api/account/code' && c.body === JSON.stringify({ email: 'anna@example.org', code: '123 456' }))).toBe(true));
    expect(await screen.findByText('My events', { selector: 'h1' })).toBeInTheDocument();
  });
  it('a wrong code says so and keeps the page', async () => {
    mockFetch({ 'POST /api/account/signup': { status: 202, body: { status: 'accepted' } },
      'POST /api/account/code': { status: 400, body: { code: 'code_invalid', field: 'code', message: { sk: 'Zlý kód', en: 'The code is wrong or no longer valid.' } } } });
    renderAt('/account/signup');
    await userEvent.type(await screen.findByLabelText('Name'), 'Anna');
    await userEvent.type(screen.getByLabelText('E-mail'), 'anna@example.org');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));
    await userEvent.type(await screen.findByLabelText('Code from the email'), '000000');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('The code is wrong or no longer valid.')).toBeInTheDocument();
    expect(screen.getByLabelText('Code from the email')).toBeInTheDocument();
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
    expect(screen.getByRole('button', { name: 'Send a sign-in code' })).toBeInTheDocument();
  });

  it('creating an event posts to /api/events/ (not the address browsers may remember a redirect for) and opens the new event', async () => {
    const me = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
    const calls = mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } },
      'POST /api/events/': { status: 201, body: { eventId: 'e9' } }, 'GET /api/events/e9/manage': { body: manage({ eventId: 'e9', name: 'Nové' }) } });
    renderAt('/org');
    await userEvent.type(await screen.findByLabelText('Name'), 'Nové');
    await userEvent.type(screen.getByLabelText('Location'), 'Žilina');
    await userEvent.type(screen.getByLabelText('Date'), '08/11/2026');
    await userEvent.type(screen.getByLabelText('Time'), '10:00');
    await userEvent.click(screen.getByRole('button', { name: 'Create event' }));
    expect(await screen.findByRole('heading', { name: 'Nové' })).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/events/'))).toBe(true);
    expect(calls.some((c) => c.url.includes('undefined'))).toBe(false);
  });
  it('a create answer without an event id shows an error instead of opening an undefined event', async () => {
    const me = { name: 'Anna', email: 'a@x', language: 'en', emailConfirmed: true, emailMode: 'smtp' };
    const calls = mockFetch({ 'GET /api/account/session': { body: me }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } },
      'POST /api/events/': { body: { emailMode: 'smtp', events: [] } } });
    renderAt('/org');
    await userEvent.type(await screen.findByLabelText('Name'), 'Nové');
    await userEvent.type(screen.getByLabelText('Location'), 'Žilina');
    await userEvent.type(screen.getByLabelText('Date'), '08/11/2026');
    await userEvent.type(screen.getByLabelText('Time'), '10:00');
    await userEvent.click(screen.getByRole('button', { name: 'Create event' }));
    expect(await screen.findByText('The event could not be created. Reload the page and try again.')).toBeInTheDocument();
    expect(calls.some((c) => c.url.includes('undefined'))).toBe(false);
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
    eventId: 'e1', name: 'Derby', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'draft', laps: 1, pointToPoint: false, spectatorDelaySeconds: 30,
    course: { lapLengthM: 12400, ascentM: 310, descentM: 305, pointCount: 4, closed: true, uploadedAt: '2026-10-30T10:00:00Z', checkpoints: [{ name: 'Bufet', lapDistanceM: 3000 }] },
    categories: [], waves: [], participants: { invited: 3, confirmed: 9, declined: 1, emailFailed: 2 }, running: false, emailMode: 'smtp',
    createdAt: '2026-10-30T10:00:00Z', endedAt: null, confirmationDeadline: null, ...over,
  });
  const PREVIEW = {
    lapLengthM: 12400, laps: 1, pointToPoint: false, closed: true, hasElevation: true, minElevationM: 290, maxElevationM: 340, ascentM: 310, descentM: 305,
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

  it('the organizer deletes an event only after typing its name and lands on the event list', async () => {
    const calls = mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage({ state: 'ready' }) },
      'GET /api/events/e1/course-preview': { body: PREVIEW }, 'DELETE /api/events/e1': { status: 204 }, 'GET /api/events': { body: { emailMode: 'smtp', events: [] } } });
    renderAt('/org/events/e1');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete the event…' }));
    expect(screen.getByText(/The participants \(15\) are not told/)).toBeInTheDocument();
    const confirm = screen.getByRole('button', { name: 'Delete for good' });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/To confirm, type the event name/), 'Derb');
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/To confirm, type the event name/), 'y');
    await userEvent.click(confirm);
    expect(await screen.findByText('Create your first event')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'DELETE' && c.url.endsWith('/api/events/e1'))).toBe(true);
  });
  it('a live event offers no delete', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage({ state: 'live', running: true }) }, 'GET /api/events/e1/course-preview': { body: PREVIEW } });
    renderAt('/org/events/e1');
    expect(await screen.findByText('Race control', { selector: 'span' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete the event…' })).toBeNull();
  });

  it('saving categories drops a row left empty and confirms the save in the section', async () => {
    const saved = manage({ categories: [{ id: 'c1', name: 'Muži', gender: 'male', minAge: null, maxAge: null }, { id: 'c2', name: 'Ženy', gender: 'female', minAge: null, maxAge: null }] });
    const calls = mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'PUT /api/events/e1/categories': { body: saved } });
    renderAt('/org/events/e1');
    const add = await screen.findByRole('button', { name: 'Add category' });
    await userEvent.click(add); await userEvent.click(add); await userEvent.click(add);
    const names = screen.getAllByPlaceholderText('Name');
    await userEvent.type(names[names.length - 3], 'Muži');
    await userEvent.type(names[names.length - 2], 'Ženy');
    await userEvent.click(screen.getByRole('button', { name: 'Save categories' }));
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    const put = calls.find((c) => c.method === 'PUT' && c.url.endsWith('/categories'))!;
    expect(JSON.parse(put.body!).map((c: { name: string }) => c.name)).toEqual(['Muži', 'Ženy']);
  });
  it('a refused category save shows the reason next to the categories', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'PUT /api/events/e1/categories': { status: 400, body: { code: 'validation_failed', field: '[0].minAge', message: { sk: 'Vek', en: 'Ages must be between 0 and 120' } } } });
    renderAt('/org/events/e1');
    await userEvent.click(await screen.findByRole('button', { name: 'Add category' }));
    await userEvent.type(screen.getAllByPlaceholderText('Name').at(-1)!, 'Deti');
    await userEvent.click(screen.getByRole('button', { name: 'Save categories' }));
    const message = await screen.findByText('Ages must be between 0 and 120');
    expect(message.closest('form')).toBe(screen.getByRole('button', { name: 'Save categories' }).closest('form'));
    expect(screen.queryByText('Saved.')).toBeNull();
  });

  it('the organizer sees the spectator link once the event is ready, not in draft', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage({ state: 'ready' }) }, 'GET /api/events/e1/course-preview': { body: PREVIEW } });
    renderAt('/org/events/e1');
    expect(await screen.findByText('Public link for spectators:')).toBeInTheDocument();
  });
  it('the organizer sees no spectator link while the event is a draft', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW } });
    renderAt('/org/events/e1');
    expect(await screen.findByText('What next')).toBeInTheDocument();
    expect(screen.queryByText('Public link for spectators:')).toBeNull();
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

  it('a race from A to B has no laps: the laps field goes away, the course is described without laps and saved as one pass', async () => {
    const calls = mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'PUT /api/events/e1/course': { body: manage({ pointToPoint: true }) } });
    renderAt('/org/events/e1');
    await screen.findByText('Checkpoints');
    expect(screen.getByLabelText('Laps (1–10)')).toBeInTheDocument();
    expect(screen.getByText('Lap length')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Race type'), 'a-b');
    expect(screen.queryByLabelText('Laps (1–10)')).toBeNull();
    expect(screen.getByText('Course length')).toBeInTheDocument();
    expect(screen.getByText(/finish at its end \(point B\)/)).toBeInTheDocument();
    expect(screen.getByLabelText('Distance from the start (km)')).toHaveValue('3');
    expect(screen.getByRole('button', { name: 'Add a point at half of the course' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/events/e1/course')).toBe(true));
    const body = JSON.parse(calls.find((c) => c.method === 'PUT' && c.url === '/api/events/e1/course')!.body!);
    expect(body).toMatchObject({ pointToPoint: true, laps: 1 });
    expect(await screen.findByText('From A to B')).toBeInTheDocument(); // the event summary of the saved race
  });

  it('more than one lap of an open course is refused before saving', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: manage({ course: { ...manage().course, closed: false } }) },
      'GET /api/events/e1/course-preview': { body: { ...PREVIEW, closed: false } } });
    renderAt('/org/events/e1');
    const laps = await screen.findByLabelText('Laps (1–10)');
    await userEvent.clear(laps);
    await userEvent.type(laps, '2');
    expect(await screen.findByText(/can be ridden only once/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
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

  it('the administrator sees every event with its organizer, the time organizers spent, and opens an event as its organizer', async () => {
    const boss = { ...ME, isAdmin: true };
    const calls = mockFetch({ 'GET /api/account/session': { body: boss }, 'GET /api/events/e1/manage': { body: manage() }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'GET /api/admin/events': { body: [{ eventId: 'e1', name: 'Anna race', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'ready', laps: 1, lapLengthM: 5000,
        createdAt: '2026-10-01T10:00:00Z', endedAt: null, organizerId: 'o1', organizerName: 'Anna', organizerEmail: 'anna@example.org', participants: { invited: 2, confirmed: 5, declined: 0, emailFailed: 0 } }] },
      'GET /api/admin/organizers': { body: [{ organizerId: 'o1', name: 'Anna', email: 'anna@example.org', createdAt: '2026-10-01T10:00:00Z', emailConfirmed: true, events: 1,
        activeSeconds: 8100, activeSecondsLast30Days: 1800, lastSeenAt: '2026-11-01T08:00:00Z' }] } });
    renderAt('/admin');
    expect(await screen.findByText('Administration')).toBeInTheDocument();
    expect(await screen.findByText('anna@example.org')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Anna race' })).toHaveAttribute('href', '/admin/organizers/o1/events/e1');
    await userEvent.click(screen.getByRole('button', { name: 'Organizers' }));
    expect(await screen.findByText('2 h 15 min')).toBeInTheDocument();
    expect(screen.getByText('30 min')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Events' }));
    await userEvent.click(await screen.findByRole('link', { name: 'Anna race' }));
    expect(await screen.findByText(/you are editing the event of/)).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.url === '/api/events/e1/manage' && c.headers['X-Act-As-Organizer'] === 'o1')).toBe(true));
  });

  it('the admin page is refused for an ordinary organizer', async () => {
    mockFetch({ 'GET /api/account/session': { body: ME } });
    renderAt('/admin');
    expect(await screen.findByText('This page is for the administrator only.')).toBeInTheDocument();
  });

  it('the participant page shows the invitation state and a link problem for an expired link', async () => {
    mockFetch({ 'GET /api/p/good': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Invited', locked: { gender: false }, phoneSetUp: false }, language: 'en' } },
      'GET /api/p/old': { status: 400, body: { code: 'link_expired', message: { sk: 'Platnosť vypršala', en: 'This link has expired' } } } });
    const first = renderAt('/p?token=good');
    expect(await screen.findByText('Time trial')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Confirm participation' })).toBeInTheDocument();
    first.unmount();
    renderAt('/p?token=old');
    expect(await screen.findByText('This link has expired')).toBeInTheDocument();
  });
  it('an expired participant link offers to mail a new one', async () => {
    const calls = mockFetch({
      'GET /api/p/old': { status: 400, body: { code: 'link_expired', message: { sk: 'Platnosť vypršala', en: 'This link has expired' } } },
      'POST /api/p/old/renew': { status: 202, body: { status: 'accepted' } } });
    renderAt('/p?token=old');
    await userEvent.click(await screen.findByRole('button', { name: 'Email me a new link' }));
    expect(await screen.findByText('We have sent a new link to your email address.')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/p/old/renew'))).toBe(true);
  });
  it('a confirmed participant gets a spectator link that follows them', async () => {
    mockFetch({ 'GET /api/p/ok': { body: { event: { id: 'e1', name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { id: 'p7', name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: false }, phoneSetUp: false }, language: 'en', link: null } },
      'GET /api/p/ok/link': { body: { link: null } } });
    renderAt('/p?token=ok');
    expect(await screen.findByRole('link', { name: /\/e\/e1\?follow=p7$/ })).toBeInTheDocument();
  });
  it('a confirmed participant sees whether the data leave the phone, and what to fix', async () => {
    const link = { level: 'warning', lastBatchAgoSeconds: 2, lastPositionAgoSeconds: 2, accuracyM: 4, clockAheadSeconds: 71, batteryPct: 80, problems: ['clock'], earlyOnly: false };
    mockFetch({ 'GET /api/p/ok': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: false }, phoneSetUp: false }, language: 'en', link } },
      'GET /api/p/ok/link': { body: { link } } });
    renderAt('/p?token=ok');
    expect(await screen.findByText('Data are coming, but with a problem')).toBeInTheDocument();
    expect(screen.getByText(/phone clock is 71 s ahead/)).toBeInTheDocument();
  });

  it('race control of a finished event says it has ended, shows the result counts and loads the frozen view once', async () => {
    const calls = mockFetch({ 'GET /api/account/session': { body: ME },
      'GET /api/events/e1/manage': { body: manage({ state: 'finished', running: true, endedAt: '2026-11-08T12:30:00Z' }) },
      'GET /api/events/e1/live': { body: { at: '2026-11-08T12:30:00Z', eventId: 'e1', participants: [], countLive: 0, countDelayed: 0, countOffline: 3, countDnf: 0 } },
      'GET /api/events/e1/results': { body: { eventId: 'e1', name: 'Derby', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', state: 'finished',
        provisional: false, endedAt: '2026-11-08T12:30:00Z', laps: 1, pointToPoint: false, finished: 5, dnf: 2, dns: 1, rows: [] } } });
    renderAt('/org/events/e1/control');
    expect(await screen.findByText('The event has finished')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Show the results' })).toHaveAttribute('href', '/org/events/e1/results');
    expect((await screen.findByText('Finished at')).previousSibling).toHaveTextContent('13:30');
    expect((await screen.findByText('DNS')).previousSibling).toHaveTextContent('1');
    expect(screen.queryByText('Broadcasting')).toBeNull();
    await new Promise((r) => setTimeout(r, 2500));
    expect(calls.filter((c) => c.url.endsWith('/api/events/e1/live')).length).toBe(1);
  });
  it('the spectator page of a finished event no longer in memory shows the results with names only where the server gives them', async () => {
    mockFetch({
      'GET /api/events/e1/course': { status: 404, body: { code: 'not_found' } },
      'GET /api/events/e1/public': { status: 404, body: { code: 'not_found' } },
      'GET /api/events/e1/info': { body: { state: 'finished', name: 'Time trial', location: 'Zilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', endedAt: '2026-11-08T12:30:00Z' } },
      'GET /api/events/e1/public-results': { body: { finished: 1, dnf: 1, dns: 0, rows: [
        { rank: 1, categoryRank: 1, bib: '7', name: 'Jana', category: 'Open', status: 'finished', netTime: '01:15:00', gapToLeader: '00:00:00', distanceM: 30000 },
        { rank: null, categoryRank: null, bib: '9', name: '#9', category: 'Open', status: 'dnf', netTime: null, gapToLeader: null, distanceM: 12000 }] } },
    });
    renderAt('/e/e1');
    expect(await screen.findByRole('heading', { name: 'Time trial' })).toBeInTheDocument();
    expect(screen.getByText('The event has finished')).toBeInTheDocument();
    expect(await screen.findByText('Jana')).toBeInTheDocument();
    expect(screen.getByText('1:15:00')).toBeInTheDocument();
    expect(screen.getByText('Winner')).toBeInTheDocument();
    expect(screen.getByText('DNF · 12.00 km')).toBeInTheDocument();
    expect(screen.queryByText('The event is not running yet')).toBeNull();
  });
  it('a participant of a finished event sees their own result above the stop panel', async () => {
    mockFetch({ 'GET /api/p/done': { body: { event: { id: 'e1', name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', finished: true },
      me: { id: 'p7', name: 'Jana', bib: '7', category: 'Open', gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: true }, phoneSetUp: true }, language: 'en', link: null,
      result: { status: 'finished', rank: 2, categoryRank: 1, netTime: '01:30:00', gapToLeader: '00:05:00', distanceM: 30000, finishers: 12 } } } });
    renderAt('/p?token=done');
    expect(await screen.findByText('Your result')).toBeInTheDocument();
    expect(screen.getByText('Place of 12').previousSibling).toHaveTextContent('2.');
    expect(screen.getByText('Net time').previousSibling).toHaveTextContent('1:30:00');
    expect(screen.getByText('Behind the winner').previousSibling).toHaveTextContent('+5:00');
    expect(screen.getByRole('link', { name: 'All results' })).toHaveAttribute('href', '/e/e1?follow=p7');
    expect(screen.getByText('Switch off location tracking')).toBeInTheDocument();
  });
  it('a participant who did not finish sees DNF and the distance', async () => {
    mockFetch({ 'GET /api/p/dnf': { body: { event: { id: 'e1', name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', finished: true },
      me: { id: 'p7', name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: true }, phoneSetUp: true }, language: 'en', link: null,
      result: { status: 'dnf', rank: null, categoryRank: null, netTime: null, gapToLeader: null, distanceM: 12345, finishers: 12 } } } });
    renderAt('/p?token=dnf');
    expect(await screen.findByText('You did not reach the finish')).toBeInTheDocument();
    expect(screen.getByText('12.35 km')).toBeInTheDocument();
  });

  it('the stop link is the fixed OwnTracks quiet-mode config', () => {
    expect(STOP_DEEP_LINK).toBe('owntracks:///config?inline=eyJfdHlwZSI6ImNvbmZpZ3VyYXRpb24iLCJtb25pdG9yaW5nIjotMX0%3D');
  });

  it('the public stop page offers the stop link in the language from the email', async () => {
    mockFetch({});
    renderAt('/stop-tracking?lang=sk', 'en');
    const button = await screen.findByRole('link', { name: 'Vypnúť sledovanie v OwnTracks' });
    expect(button).toHaveAttribute('href', STOP_DEEP_LINK);
    expect(screen.getByText(/Quiet \(Tichý\)/)).toBeInTheDocument();
  });

  it('after the finish the participant page shows how to switch tracking off instead of the setup steps', async () => {
    const calls = mockFetch({ 'GET /api/p/done': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling', finished: true }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: true }, phoneSetUp: false }, language: 'en', link: null } } });
    renderAt('/p?token=done');
    expect(await screen.findByRole('heading', { name: 'Switch off location tracking' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Switch off tracking in OwnTracks' })).toHaveAttribute('href', STOP_DEEP_LINK);
    expect(screen.queryByRole('button', { name: 'Agree and continue' })).toBeNull();
    expect(calls.some((c) => c.url.includes('/owntracks-config') || c.url.endsWith('/link'))).toBe(false);
  });

  it('a returning participant with a set-up phone sees the state and sets the phone up again only on request', async () => {
    const link = { level: 'good', lastBatchAgoSeconds: 2, lastPositionAgoSeconds: 2, accuracyM: 4, clockAheadSeconds: null, batteryPct: 80, problems: [], earlyOnly: false };
    mockFetch({ 'GET /api/p/back': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: false }, phoneSetUp: true }, language: 'en', link } },
      'GET /api/p/back/link': { body: { link } } });
    renderAt('/p?token=back');
    expect(await screen.findByText('You are registered and your phone is set up.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Agree and continue' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Set up the phone again' }));
    expect(screen.getByText(/replaces the previous setup/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Agree and continue' })).toBeInTheDocument();
  });

  it('a silent phone is told what to check', async () => {
    const link = { level: 'silent', lastBatchAgoSeconds: 130, lastPositionAgoSeconds: 130, accuracyM: null, clockAheadSeconds: null, batteryPct: 13, problems: [], earlyOnly: false };
    mockFetch({ 'GET /api/p/quiet': { body: { event: { name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T10:00:00Z', sport: 'cycling' }, me: { name: 'Jana', bib: '7', category: null, gender: 'female', nickname: null, status: 'Confirmed', locked: { gender: false }, phoneSetUp: false }, language: 'en', link } },
      'GET /api/p/quiet/link': { body: { link } } });
    renderAt('/p?token=quiet');
    expect(await screen.findByText('The server has received nothing from you for 2 min.')).toBeInTheDocument();
    expect(screen.getByText(/keep the screen on/)).toBeInTheDocument();
  });
  it('the organizer sees the results of a finished event with ranks, times and who has no consent, and can export them', async () => {
    const line = (o: object) => ({ participantId: 'p', rank: 1, categoryRank: 1, genderRank: 1, bib: '7', name: 'Jana Nováková', nickname: null, gender: 'female', category: 'Open', wave: null,
      isMinor: false, nameConsent: false, status: 'finished', start: '2026-11-08T09:00:00Z', finishTime: '2026-11-08T10:15:00Z', netTime: '01:15:00', gapToLeader: '00:00:00',
      distanceM: 37500, laps: 3, avgSpeedMps: 8.3, maxSpeedMps: 12, movingTime: '01:10:00', stoppedTime: '00:05:00', ascentM: 410, descentM: 410,
      splits: [{ lap: 1, segmentIndex: 0, time: '00:10:00', colour: 'green' }], ...o });
    const view = { eventId: 'e1', name: 'Time trial', location: 'Žilina', startTime: '2026-11-08T09:00:00Z', sport: 'cycling', state: 'finished', provisional: false, endedAt: '2026-11-08T10:30:00Z',
      laps: 3, finished: 2, dnf: 1, dns: 0,
      rows: [line({}), line({ participantId: 'q', rank: 2, categoryRank: 2, bib: '9', name: 'Peter Kráľ', nameConsent: true, netTime: '01:20:00', gapToLeader: '00:05:00', startObservedM: 10600, startMissed: true }),
        line({ participantId: 'r', rank: null, categoryRank: null, bib: '11', name: 'Adam Nový', status: 'dnf', netTime: null, distanceM: 12000, nameConsent: true })] };
    mockFetch({ 'GET /api/account/session': { body: ME }, 'GET /api/events/e1/manage': { body: { ...manage(), state: 'finished' } }, 'GET /api/events/e1/course-preview': { body: PREVIEW },
      'GET /api/events/e1/results': { body: view } });
    renderAt('/org/events/e1/results');
    expect(await screen.findByText('Final')).toBeInTheDocument();
    expect(screen.getByText('Jana Nováková')).toBeInTheDocument();
    expect(screen.getByText(/Without consent: 1/)).toBeInTheDocument();
    expect(screen.getByText('Winner')).toBeInTheDocument();
    expect(screen.getByText(/start not recorded \(first position at 10.6 km\)/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download CSV' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'DNF' }));
    expect(screen.queryByText('Jana Nováková')).toBeNull();
    expect(screen.getByText('Adam Nový')).toBeInTheDocument();
  });
});
