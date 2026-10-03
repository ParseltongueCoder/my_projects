// Mirrors Admin.Api/Data/Dtos.cs. Timestamps are ISO strings in UTC.

export interface Producer {
  id: number;
  name: string;
  state: string;
  downReason: string | null;
  lastProcessedFeedTs: string | null;
  updatedAt: string;
}

export interface Overview {
  producers: Producer[];
  liveEvents: number;
  upcomingEvents: number;
  activeMarkets: number;
  suspendedMarkets: number;
  messagesLastHour: number;
  failedLastHour: number;
  lastMessageAt: string | null;
}

export interface EventSummary {
  id: number;
  urn: string | null;
  name: string;
  sport: string;
  category: string | null;
  tournament: string | null;
  home: string | null;
  away: string | null;
  scheduledAt: string | null;
  status: string;
  matchStatusCode: number | null;
  homeScore: number | null;
  awayScore: number | null;
  clock: string | null;
  lastFeedAt: string | null;
  markets: number;
  activeMarkets: number;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  hasMore: boolean;
}

export interface Outcome {
  code: string;
  name: string;
  odds: number | null;
  probability: number | null;
  active: boolean;
  result: string | null;
  voidFactor: number | null;
  deadHeatFactor: number | null;
  certainty: number | null;
}

export interface Market {
  id: number;
  marketTypeId: string | null;
  name: string;
  template: string;
  specifiers: string;
  /** What we offer. */
  status: string;
  /** What the feed last said (differs while the producer is down). */
  feedStatus: string;
  producerId: number | null;
  favourite: boolean;
  lastFeedAt: string;
  outcomes: Outcome[];
}

export interface EventDetail {
  event: EventSummary;
  markets: Market[];
}

export interface Settlement {
  id: number;
  marketId: number;
  market: string;
  outcomeCode: string;
  outcome: string;
  result: string;
  certainty: number;
  voidFactor: number | null;
  deadHeatFactor: number | null;
  producerId: number;
  feedTs: string;
  rolledBackAt: string | null;
  supersededById: number | null;
  state: 'effective' | 'superseded' | 'rolled_back';
}

export interface BetStop {
  id: number;
  source: string;
  groups: string | null;
  targetStatus: string;
  affectedMarkets: number | null;
  producerId: number;
  feedTs: string;
}

export interface FeedMessage {
  id: number;
  receivedAt: string;
  type: string;
  eventUrn: string | null;
  eventId: number | null;
  producerId: number | null;
  requestId: number | null;
  feedTs: string | null;
  status: string;
  error: string | null;
  lagMs: number | null;
}

export interface FeedMessageDetail {
  message: FeedMessage;
  payload: string;
}

export interface Me {
  name: string | null;
  roles: string[];
}

export interface SimulatorStatus {
  enabled?: boolean;
  producers?: { id: number; mode: string; until: string | null }[];
  replay?: { source: string | null; published: number; total: number; running: boolean };
}

/** A canonical-model change pushed over SSE (see V004 migration). */
export interface Change {
  table: 'event' | 'market' | 'producer_status';
  eventId: number | null;
  producerId: number | null;
}
