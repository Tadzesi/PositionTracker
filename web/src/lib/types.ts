export type EventState = 'draft' | 'ready' | 'live' | 'finished';
export type Sport = 'running' | 'cycling';

export interface Me { name: string; email: string; language: string; emailConfirmed: boolean; emailMode: string; isAdmin?: boolean }
export interface ParticipantCounts { invited: number; confirmed: number; declined: number; emailFailed: number }
export interface EventSummary {
  eventId: string; name: string; location: string; startTime: string; sport: Sport; state: EventState;
  /** A point-to-point race (start A, finish B, no laps; laps is 1). */
  laps: number; pointToPoint: boolean; lapLengthM: number | null; participants: ParticipantCounts;
}
export interface CheckpointPlace { name: string; lapDistanceM: number; lat: number; lon: number }
/** GET /api/events/{id}/course-preview: points are [lat, lon, elevation|null, distance from the lap start]. */
export interface CoursePreview {
  lapLengthM: number; laps: number; pointToPoint: boolean; closed: boolean; hasElevation: boolean; minElevationM: number | null; maxElevationM: number | null;
  ascentM: number; descentM: number; points: [number, number, number | null, number][]; checkpoints: CheckpointPlace[];
}
export interface Checkpoint { name: string; lapDistanceM: number }
export interface CourseView { lapLengthM: number; ascentM: number; descentM: number; pointCount: number; closed: boolean; uploadedAt: string; checkpoints: Checkpoint[] }
export interface CategoryView { id: string; name: string; gender: string | null; minAge: number | null; maxAge: number | null }
export interface WaveView { id: string; name: string; startTime: string }
export interface ManageView {
  eventId: string; name: string; location: string; startTime: string; sport: Sport; state: EventState; laps: number; pointToPoint: boolean;
  spectatorDelaySeconds: number; course: CourseView | null; categories: CategoryView[]; waves: WaveView[];
  participants: ParticipantCounts;
  running: boolean; emailMode: string; createdAt: string; endedAt: string | null; confirmationDeadline: string | null;
}
export type LinkProblem = 'clock' | 'accuracy' | 'lowBattery' | 'rejected';
/** The state of a participant's data link (the traffic light); ages are seconds on the server clock. */
export interface LinkHealth {
  level: 'waiting' | 'good' | 'warning' | 'silent'; lastBatchAgoSeconds: number | null; lastPositionAgoSeconds: number | null;
  accuracyM: number | null; clockAheadSeconds: number | null; batteryPct: number | null; problems: LinkProblem[]; earlyOnly: boolean;
}
export interface ParticipantRow {
  participantId: string; bib: string; name: string; nickname: string | null; nicknameHidden: boolean; email: string; gender: string;
  link?: LinkHealth | null; language: string; category: string | null; wave: string | null; status: 'Invited' | 'Confirmed' | 'Declined' | 'EmailFailed'; isMinor: boolean; createdAt: string;
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
  laps: number; lapLengthM: number; closed: boolean; points: [number, number][]; profile?: [number, number][] | null; checkpoints: Checkpoint[];
}

export interface SplitLine { lap: number; segmentIndex: number; time: string; colour: 'yellow' | 'green' | 'purple' }
export interface ResultLine {
  participantId: string; rank: number | null; categoryRank: number | null; genderRank: number | null; bib: string; name: string; nickname: string | null;
  gender: string | null; category: string | null; wave: string | null; isMinor: boolean; nameConsent: boolean; status: 'finished' | 'dnf' | 'dns';
  start: string; finishTime: string | null; netTime: string | null; gapToLeader: string | null; distanceM: number; laps: number; avgSpeedMps: number;
  maxSpeedMps: number; movingTime: string; stoppedTime: string; ascentM: number; descentM: number; splits: SplitLine[]; startObservedM?: number; startMissed?: boolean;
}
export interface ResultsView {
  eventId: string; name: string; location: string; startTime: string; sport: Sport; state: string; provisional: boolean; endedAt: string | null;
  laps: number; pointToPoint: boolean; finished: number; dnf: number; dns: number; rows: ResultLine[];
}

export interface LinkView {
  event: { id: string; name: string; location: string; startTime: string; sport: Sport; finished: boolean };
  me: { id: string; name: string; bib: string; category: string | null; gender: string; nickname: string | null; status: string; locked: { gender: boolean }; phoneSetUp: boolean };
  language: string; link?: LinkHealth | null;
}

export interface AdminEvent {
  eventId: string; name: string; location: string; startTime: string; sport: string; state: string; laps: number; lapLengthM: number | null;
  createdAt: string; endedAt: string | null; organizerId: string; organizerName: string; organizerEmail: string; participants: ParticipantCounts;
}
export interface AdminOrganizer {
  organizerId: string; name: string; email: string; createdAt: string; emailConfirmed: boolean; events: number;
  activeSeconds: number; activeSecondsLast30Days: number; lastSeenAt: string | null;
}
export interface AuditRow { at: string; adminEmail: string; organizerEmail: string; method: string; path: string }
