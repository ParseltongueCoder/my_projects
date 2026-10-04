import { HttpClient, HttpErrorResponse, HttpInterceptorFn, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { APP_CONFIG } from './config';
import {
  AdminUser, AuditEntry, Brand, ChangeSet, EffectiveSetting, MarketType, Me, Operator, Page, Permission, Problem, Role,
  ScopeOption, ScopeSetting, ScopeType, SettingChange, SettingDef,
} from './models';
import { OperatorSelection } from './operator-selection';

type Params = Record<string, string | number | boolean | null | undefined>;

function params(p: Params): HttpParams {
  let hp = new HttpParams();
  for (const [k, v] of Object.entries(p)) {
    if (v !== null && v !== undefined && v !== '') {
      hp = hp.set(k, String(v));
    }
  }
  return hp;
}

/** Platform staff act for the operator chosen in the header switcher (docs/08 §1.5). */
export const operatorHeaderInterceptor: HttpInterceptorFn = (req, next) => {
  const selected = inject(OperatorSelection).selected();
  return selected && req.url.includes('/api/bo/') ? next(req.clone({ setHeaders: { 'X-Operator-Id': String(selected) } })) : next(req);
};

/** Human message for an API error: ProblemDetails title (+ code), else HTTP status text. */
export function problemMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const p = err.error as Partial<Problem> | null;
    if (p && typeof p === 'object' && p.title) {
      return p.code ? `${p.title} (${p.code})` : p.title;
    }
    return err.status === 0 ? 'API not reachable' : `${err.status} ${err.statusText}`;
  }
  return String(err);
}

@Injectable({ providedIn: 'root' })
export class BoApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBaseUrl}/api/bo`;

  me(): Observable<Me> {
    return this.http.get<Me>(`${this.base}/me`);
  }

  // CFG
  settingDefs(): Observable<SettingDef[]> {
    return this.http.get<SettingDef[]>(`${this.base}/cfg/defs`);
  }

  scopeSettings(scopeType: ScopeType, scopeId: number | null, marketTypeId: number | null): Observable<ScopeSetting[]> {
    return this.http.get<ScopeSetting[]>(`${this.base}/cfg/scope`, { params: params({ scopeType, scopeId, marketTypeId }) });
  }

  effective(query: Params): Observable<EffectiveSetting[]> {
    return this.http.get<EffectiveSetting[]>(`${this.base}/cfg/effective`, { params: params(query) });
  }

  changeSets(status?: string): Observable<ChangeSet[]> {
    return this.http.get<ChangeSet[]>(`${this.base}/cfg/change-sets`, { params: params({ status }) });
  }

  createChangeSet(title: string, platform: boolean, changes: SettingChange[]): Observable<ChangeSet> {
    return this.http.post<ChangeSet>(`${this.base}/cfg/change-sets`, { title, platform, changes });
  }

  decideChangeSet(id: number, approve: boolean, comment: string): Observable<ChangeSet> {
    return this.http.post<ChangeSet>(`${this.base}/cfg/change-sets/${id}/${approve ? 'approve' : 'reject'}`, { comment });
  }

  scopes(q: string, type?: ScopeType): Observable<ScopeOption[]> {
    return this.http.get<ScopeOption[]>(`${this.base}/cfg/scopes`, { params: params({ q, type }) });
  }

  marketTypes(q: string): Observable<MarketType[]> {
    return this.http.get<MarketType[]>(`${this.base}/cfg/market-types`, { params: params({ q }) });
  }

  // ADM
  permissions(): Observable<Permission[]> {
    return this.http.get<Permission[]>(`${this.base}/adm/permissions`);
  }

  roles(): Observable<Role[]> {
    return this.http.get<Role[]>(`${this.base}/adm/roles`);
  }

  users(): Observable<AdminUser[]> {
    return this.http.get<AdminUser[]>(`${this.base}/adm/users`);
  }

  createUser(body: { username: string; email: string; displayName: string; roles: string[] }): Observable<{ user: AdminUser; temporaryPassword: string | null }> {
    return this.http.post<{ user: AdminUser; temporaryPassword: string | null }>(`${this.base}/adm/users`, body);
  }

  setRoles(id: string, roles: string[], reason: string): Observable<AdminUser> {
    return this.http.put<AdminUser>(`${this.base}/adm/users/${id}/roles`, { roles, reason });
  }

  setUserEnabled(id: string, enabled: boolean, reason: string): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.base}/adm/users/${id}/${enabled ? 'enable' : 'disable'}`, { reason });
  }

  audit(query: Params): Observable<Page<AuditEntry>> {
    return this.http.get<Page<AuditEntry>>(`${this.base}/adm/audit`, { params: params(query) });
  }

  // Platform / tenant
  operators(): Observable<Operator[]> {
    return this.http.get<Operator[]>(`${this.base}/platform/operators`);
  }

  createOperator(body: { code: string; name: string; baseCurrency: string; currencies: string[] }): Observable<Operator> {
    return this.http.post<Operator>(`${this.base}/platform/operators`, body);
  }

  updateOperator(id: number, body: { status?: string; name?: string; reason: string }): Observable<Operator> {
    return this.http.patch<Operator>(`${this.base}/platform/operators/${id}`, body);
  }

  brands(): Observable<Brand[]> {
    return this.http.get<Brand[]>(`${this.base}/brands`);
  }

  createBrand(body: { code: string; name: string; audience: string; primaryDomain: string }): Observable<Brand> {
    return this.http.post<Brand>(`${this.base}/brands`, body);
  }

  modules(): Observable<Record<string, boolean>> {
    return this.http.get<Record<string, boolean>>(`${this.base}/modules`);
  }

  setModules(modules: Record<string, boolean>, reason: string): Observable<void> {
    return this.http.put<void>(`${this.base}/modules`, { modules, reason });
  }
}
