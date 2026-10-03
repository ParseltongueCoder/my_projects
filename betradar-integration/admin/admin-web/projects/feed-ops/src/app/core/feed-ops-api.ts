import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { APP_CONFIG } from './config';
import {
  BetStop, EventDetail, EventSummary, FeedMessage, FeedMessageDetail, Me, Overview, PagedResult, Producer, Settlement,
  SimulatorStatus,
} from './models';

export interface EventFilter {
  status?: string | null;
  q?: string | null;
  page?: number;
  pageSize?: number;
}

export interface MessageFilter {
  type?: string | null;
  status?: string | null;
  eventUrn?: string | null;
  page?: number;
  pageSize?: number;
}

/** Typed client for Admin.Api (/api/...). */
@Injectable({ providedIn: 'root' })
export class FeedOpsApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBaseUrl}/api`;

  me(): Observable<Me> {
    return this.http.get<Me>(`${this.base}/me`);
  }

  overview(): Observable<Overview> {
    return this.http.get<Overview>(`${this.base}/overview`);
  }

  producers(): Observable<Producer[]> {
    return this.http.get<Producer[]>(`${this.base}/producers`);
  }

  events(filter: EventFilter): Observable<PagedResult<EventSummary>> {
    return this.http.get<PagedResult<EventSummary>>(`${this.base}/events`, { params: toParams(filter) });
  }

  event(id: number): Observable<EventDetail> {
    return this.http.get<EventDetail>(`${this.base}/events/${id}`);
  }

  settlements(eventId: number): Observable<Settlement[]> {
    return this.http.get<Settlement[]>(`${this.base}/events/${eventId}/settlements`);
  }

  betStops(eventId: number): Observable<BetStop[]> {
    return this.http.get<BetStop[]>(`${this.base}/events/${eventId}/bet-stops`);
  }

  messages(filter: MessageFilter): Observable<PagedResult<FeedMessage>> {
    return this.http.get<PagedResult<FeedMessage>>(`${this.base}/messages`, { params: toParams(filter) });
  }

  message(id: number): Observable<FeedMessageDetail> {
    return this.http.get<FeedMessageDetail>(`${this.base}/messages/${id}`);
  }

  simulatorStatus(): Observable<SimulatorStatus> {
    return this.http.get<SimulatorStatus>(`${this.base}/sim/status`);
  }

  scenarios(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/sim/scenarios`);
  }

  startScenario(name: string, speed: number): Observable<unknown> {
    return this.http.post(`${this.base}/sim/scenarios/${encodeURIComponent(name)}`, { speed });
  }

  replayDemo(): Observable<unknown> {
    return this.http.post(`${this.base}/sim/replay`, {});
  }

  producerDown(id: number, mode: 'silent' | 'unsubscribed', seconds: number): Observable<unknown> {
    return this.http.post(`${this.base}/sim/producers/${id}/down`, null, { params: { mode, seconds } });
  }

  producerUp(id: number): Observable<unknown> {
    return this.http.post(`${this.base}/sim/producers/${id}/up`, null);
  }
}

function toParams(filter: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value !== null && value !== undefined && value !== '') {
      params = params.set(key, String(value));
    }
  }
  return params;
}
