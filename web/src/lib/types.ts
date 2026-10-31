export type EventState = 'draft' | 'ready' | 'live' | 'finished';
export type Sport = 'running' | 'cycling';

export interface Me { name: string; email: string; language: string; emailConfirmed: boolean; emailMode: string }
export interface EventSummary { eventId: string; name: string; location: string; startTime: string; sport: Sport; state: EventState }
export interface Checkpoint { name: string; lapDistanceM: number }
export interface CourseView { lapLengthM: number; ascentM: number; descentM: number; pointCount: number; closed: boolean; uploadedAt: string; checkpoints: Checkpoint[] }
export interface CategoryView { id: string; name: string; gender: string | null; minAge: number | null; maxAge: number | null }
export interface WaveView { id: string; name: string; startTime: string }
export interface ManageView {
  eventId: string; name: string; location: string; startTime: string; sport: Sport; state: EventState; laps: number;
  spectatorDelaySeconds: number; course: CourseView | null; categories: CategoryView[]; waves: WaveView[];
  participants: { invited: number; confirmed: number; declined: number; emailFailed: number };
  running: boolean; emailMode: string; createdAt: string; endedAt: string | null; confirmationDeadline: string | null;
}
export interface ParticipantRow {
  participantId: string; bib: string; name: string; nickname: string | null; nicknameHidden: boolean; email: string; gender: string;
  language: string; category: string | null; wave: string | null; status: 'Invited' | 'Confirmed' | 'Declined' | 'EmailFailed'; isMinor: boolean; createdAt: string;
}
export interface ParticipantList {
  emailMode: string; eventState: EventState;
  counts: { all: number; confirmed: number; invited: number; declined: number; emailFailed: number };
  participants: ParticipantRow[];
}

export type RState = 'notStarted' | 'live' | 'delayed' | 'offline' | 'stopped' | 'offCourse' | 'sos' | 'finished' | 'dnf';
export interface Alarm { kind: string; since: string; acknowledged: boolean }
export interface Eta { expected: string; earliest: string; latest: string; approximate: boolean }
export interface Segment { lap: number; segmentIndex: number; time: string; colour: 'yellow' | 'green' | 'purple' }
export interface LiveParticipant {
  participantId: string; bib: string; displayName: string; category: string; rank: number | null; lap: number;
  totalDistanceM: number; lapProgressM: number; lat: number | null; lon: number | null; speedMps: number; avgSpeedMps: number;
  paceSecPerKm: number | null; ascentM: number; descentM: number; gradePercent: number; state: RState;
  gapToLeader: string | null; gapToAhead: string | null; eta: Eta | null; segments: Segment[];
  batteryPct: number | null; gpsAccuracyM: number | null; queueLength: number; lastSignalAgo: string | null; alarms: Alarm[]; lapsInferred: boolean;
}
export interface LiveView { at: string; eventId: string; participants: LiveParticipant[]; countLive: number; countDelayed: number; countOffline: number; countDnf: number }
export type SpectatorParticipant = Pick<LiveParticipant,
  'participantId' | 'bib' | 'displayName' | 'category' | 'rank' | 'lap' | 'totalDistanceM' | 'lat' | 'lon' | 'speedMps' | 'paceSecPerKm' | 'state' | 'gapToLeader' | 'gapToAhead' | 'eta' | 'segments'>;
export interface SpectatorView { at: string; eventId: string; participants: SpectatorParticipant[] }
export interface PublicCourse {
  event: { name: string; sport: Sport; startTime: string; spectatorDelaySeconds: number };
  laps: number; lapLengthM: number; closed: boolean; points: [number, number][]; checkpoints: Checkpoint[];
}

export interface LinkView {
  event: { name: string; location: string; startTime: string; sport: Sport };
  me: { name: string; bib: string; category: string | null; gender: string; nickname: string | null; status: string; locked: { gender: boolean } };
  language: string;
}
