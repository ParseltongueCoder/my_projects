/** Wire types of Bo.Api (/api/bo). */

export interface OperatorRef {
  id: number;
  code: string;
  name: string;
}

export interface Me {
  user: { id: string; username: string; displayName: string | null };
  isPlatform: boolean;
  operator: OperatorRef | null;
  impersonating: boolean;
  readOnly: boolean;
  permissions: string[];
  operators: OperatorRef[];
}

export type ScopeType = 'platform' | 'operator' | 'brand' | 'sport' | 'category' | 'tournament' | 'event' | 'market';

export const SCOPE_TYPES: ScopeType[] = ['platform', 'operator', 'brand', 'sport', 'category', 'tournament', 'event', 'market'];

export type ValueType = 'bool' | 'int' | 'decimal' | 'string' | 'enum' | 'money' | 'json' | 'stringList';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type Json = any;

export interface SettingDef {
  key: string;
  module: string;
  type: ValueType;
  allowedScopes: ScopeType[];
  allowsMarketType: boolean;
  default: Json;
  combine: string;
  enumValues: string[] | null;
  min: number | null;
  max: number | null;
  operatorEditable: boolean;
  requiresApproval: boolean;
  customerCombine: string;
  description: string;
}

export interface SettingRow {
  id: number;
  operatorId: number | null;
  scopeType: ScopeType;
  scopeId: number | null;
  marketTypeId: number | null;
  key: string;
  value: Json;
  changeSetId: number;
  updatedAt: string;
}

export interface EffectiveSetting {
  key: string;
  value: Json;
  isDefault: boolean;
  winner: SettingRow | null;
  trace: { row: SettingRow; specificity: number; winner: boolean }[];
}

export interface ScopeSetting {
  key: string;
  module: string;
  valueHere: Json;
  changeSetId: number | null;
  updatedAt: string | null;
  effective: EffectiveSetting;
}

export interface SettingChange {
  op: 'upsert' | 'delete';
  scopeType: ScopeType;
  scopeId: number | null;
  marketTypeId: number | null;
  key: string;
  value?: Json;
  oldValue?: Json;
  newValue?: Json;
}

export interface ChangeSet {
  id: number;
  operatorId: number | null;
  title: string;
  status: 'pending_approval' | 'applied' | 'rejected' | 'cancelled';
  createdBy: string;
  createdByName: string | null;
  createdAt: string;
  decidedBy: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionComment: string | null;
  appliedAt: string | null;
  configVersion: number | null;
  changes: SettingChange[];
}

export interface ScopeOption {
  type: ScopeType;
  id: number;
  name: string | null;
  path: string | null;
}

export interface MarketType {
  id: number;
  name: string;
}

export interface Brand {
  id: number;
  operatorId: number;
  code: string;
  name: string;
  audience: 'all' | 'local' | 'foreign';
  primaryDomain: string | null;
  status: string;
  version: number;
}

export interface Operator {
  id: number;
  code: string;
  name: string;
  status: string;
  timezone: string;
  baseCurrency: string;
  currencies: string[];
  languages: string[];
  jurisdiction: string;
  createdAt: string;
  version: number;
}

export interface AdminUser {
  id: string;
  operatorId: number | null;
  username: string;
  email: string;
  displayName: string | null;
  status: 'invited' | 'active' | 'disabled';
  lastLoginAt: string | null;
  createdAt: string;
  version: number;
  roles: string[];
}

export interface Role {
  id: number;
  operatorId: number | null;
  code: string;
  name: string;
  isSystem: boolean;
  isPlatform: boolean;
  permissions: string[];
}

export interface Permission {
  code: string;
  module: string;
  description: string;
  risk: 'normal' | 'sensitive' | 'critical';
  platformOnly: boolean;
}

export interface AuditEntry {
  id: number;
  ts: string;
  operatorId: number | null;
  actorId: string | null;
  actorType: string;
  actorName: string | null;
  action: string;
  entityType: string;
  entityId: string;
  before: Json;
  after: Json;
  reason: string | null;
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

/** RFC 9457 problem with our stable code. */
export interface Problem {
  status: number;
  title: string;
  code?: string;
}

// ---- BO-1: catalogue, translations, media

export interface MediaRef {
  role: 'icon' | 'flag' | 'logo' | 'banner';
  mediaId: string;
  url: string;
  inherited: boolean;
}

export interface TreeNode {
  type: 'sport' | 'category' | 'tournament';
  id: number;
  feedName: string;
  name: string;
  nameSource: 'operator' | 'platform' | 'provider' | 'id';
  visible: boolean;
  hiddenHere: boolean;
  sortOrder: number | null;
  isTop: boolean;
  topOrder: number | null;
  slug: string | null;
  openEvents: number;
  countryCode: string | null;
  media: MediaRef[];
  children: TreeNode[];
}

export interface CatalogEvent {
  id: number;
  urn: string | null;
  name: string;
  tournamentName: string | null;
  tournamentId: number | null;
  sportId: number;
  sportName: string;
  scheduledAt: string | null;
  displayStartAt: string | null;
  status: string;
  isFeatured: boolean;
  openMarkets: number;
}

export interface EventOverride {
  displayStartAt: string | null;
  isFeatured: boolean;
  featuredOrder: number | null;
  featuredFrom: string | null;
  featuredTo: string | null;
  note: string | null;
  version: number;
}

export interface EventDetail {
  event: CatalogEvent;
  override: EventOverride | null;
  competitors: { id: number; position: number; qualifier: string | null; name: string }[];
  policy: Record<string, Json>;
}

export interface Participant {
  id: number;
  feedName: string;
  name: string;
  nameSource: string;
  shortName: string | null;
  abbreviation: string | null;
  countryCode: string | null;
  sportId: number | null;
  events: number;
  media: MediaRef[];
}

export type I18nEntityType = 'sport' | 'category' | 'tournament' | 'competitor' | 'market_type' | 'outcome_type';

export interface TranslationCell {
  operator: string | null;
  platform: string | null;
  provider: string | null;
}

export interface TranslationRow {
  entityType: I18nEntityType;
  entityId: string;
  field: string;
  source: string;
  context: string | null;
  values: Record<string, TranslationCell>;
}

export interface TranslationEdit {
  entityType: string;
  entityId: string;
  field: string;
  lang: string;
  text: string | null;
  platform: boolean;
}

export interface TemplatePreview {
  market: { template: string; source: string; rendered: string };
  outcomes: { code: string; template: string; source: string; rendered: string }[];
}
