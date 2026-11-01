import { lazy } from 'react';
import { Route, Routes, Navigate } from 'react-router-dom';
import Layout from './components/Layout';
import { ConfirmEmail, Home, SignIn, SignUp } from './pages/Account';
import OrgEvents, { RequireSession } from './pages/OrgEvents';

// Route-level code splitting: the home and account pages are in the entry bundle; everything else (and with it Leaflet, which only
// the map pages use, and the QR code library, which only the participant page uses) is fetched when its route is opened.
const EventLayout = lazy(() => import('./pages/EventLayout'));
const Setup = lazy(() => import('./pages/EventLayout').then((m) => ({ default: m.Setup })));
const Participants = lazy(() => import('./pages/Participants'));
const RaceControl = lazy(() => import('./pages/RaceControl'));
const ParticipantPage = lazy(() => import('./pages/Participant'));
const Spectator = lazy(() => import('./pages/Spectator'));

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Home />} />
        <Route path="account/signup" element={<SignUp />} />
        <Route path="account/signin" element={<SignIn />} />
        <Route path="account/confirm" element={<ConfirmEmail />} />
        <Route path="org" element={<RequireSession><OrgEvents /></RequireSession>} />
        <Route path="org/events/:id" element={<RequireSession><EventLayout /></RequireSession>}>
          <Route index element={<Setup />} />
          <Route path="participants" element={<Participants />} />
          <Route path="control" element={<RaceControl />} />
        </Route>
        <Route path="p" element={<ParticipantPage />} />
        <Route path="e/:id" element={<Spectator />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
