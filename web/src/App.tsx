import { Route, Routes, Navigate } from 'react-router-dom';
import Layout from './components/Layout';
import { ConfirmEmail, Home, SignIn, SignUp } from './pages/Account';
import OrgEvents, { RequireSession } from './pages/OrgEvents';
import EventLayout, { Setup } from './pages/EventLayout';
import Participants from './pages/Participants';
import RaceControl from './pages/RaceControl';
import ParticipantPage from './pages/Participant';
import Spectator from './pages/Spectator';

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
