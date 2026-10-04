-- V005: operator back office foundation (BO-0): tenants, admin users & RBAC, audit, outbox, configuration engine.
-- Design: docs/08 §1.4 / §3 (tenancy, ADM), docs/06 §5 (CFG), docs/09 §2 (binding decisions).
--
-- Tenant isolation is enforced by PostgreSQL row-level security. Bo.Api runs every request in a transaction that
-- does `SET LOCAL ROLE bo_app` (RLS applies to it) and `set_config('app.operator_id', ..., true)`. Without an
-- operator setting the tenant policies match no rows (fail closed). `app.platform = on` marks platform staff:
-- it unlocks platform-level rows (operator list, platform settings, platform users), never other tenants' rows.

CREATE SCHEMA IF NOT EXISTS bo;
SET search_path = bo;

-- Roles are cluster-wide: several databases (tests) may migrate at the same time.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bo_app') THEN
    CREATE ROLE bo_app NOLOGIN;
  END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
  NULL;
END $$;

CREATE FUNCTION bo.current_operator() RETURNS bigint LANGUAGE sql STABLE
  AS $$ SELECT nullif(current_setting('app.operator_id', true), '')::bigint $$;
CREATE FUNCTION bo.is_platform() RETURNS boolean LANGUAGE sql STABLE
  AS $$ SELECT coalesce(current_setting('app.platform', true), '') = 'on' $$;

-- =====================================================================
-- Tenants
-- =====================================================================
CREATE TABLE operator (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  code           text NOT NULL UNIQUE CHECK (code ~ '^[a-z0-9][a-z0-9-]{1,39}$'),  -- = Keycloak organization alias
  name           text NOT NULL,
  status         text NOT NULL DEFAULT 'onboarding' CHECK (status IN ('onboarding', 'active', 'suspended', 'terminated')),
  timezone       text NOT NULL DEFAULT 'Asia/Tbilisi',
  base_currency  char(3) NOT NULL DEFAULT 'GEL',
  currencies     char(3)[] NOT NULL DEFAULT '{GEL}',
  languages      text[] NOT NULL DEFAULT '{ka,en,ru}',
  jurisdiction   text NOT NULL DEFAULT 'GE',
  created_at     timestamptz NOT NULL DEFAULT now(),
  version        integer NOT NULL DEFAULT 1
);

-- A site / domain of an operator (docs/09 §2.1: Georgia requires separate local and foreign-player domains).
CREATE TABLE brand (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id     bigint NOT NULL REFERENCES operator(id),
  code            text NOT NULL CHECK (code ~ '^[a-z0-9][a-z0-9-]{1,39}$'),
  name            text NOT NULL,
  audience        text NOT NULL DEFAULT 'all' CHECK (audience IN ('all', 'local', 'foreign')),
  primary_domain  text,
  status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'disabled')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  version         integer NOT NULL DEFAULT 1,
  UNIQUE (operator_id, code)
);

CREATE TABLE operator_module (
  operator_id  bigint NOT NULL REFERENCES operator(id),
  module_code  text NOT NULL,
  enabled      boolean NOT NULL,
  PRIMARY KEY (operator_id, module_code)
);

-- =====================================================================
-- Admin users and RBAC (identity lives in Keycloak realm "bo"; permissions live here)
-- =====================================================================
CREATE TABLE admin_user (
  id                 uuid PRIMARY KEY,                       -- Keycloak user id (token "sub")
  operator_id        bigint REFERENCES operator(id),         -- NULL = platform staff
  username           text NOT NULL,
  email              text NOT NULL,
  display_name       text,
  status             text NOT NULL DEFAULT 'active' CHECK (status IN ('invited', 'active', 'disabled')),
  allowed_operators  bigint[],                               -- platform staff only; NULL = all operators
  ui_prefs           jsonb NOT NULL DEFAULT '{}'::jsonb,
  last_login_at      timestamptz,
  created_at         timestamptz NOT NULL DEFAULT now(),
  version            integer NOT NULL DEFAULT 1,
  UNIQUE NULLS NOT DISTINCT (operator_id, username)
);

-- Catalogs below are kept in sync from code at startup (Bo.Api Permissions / SettingCatalog).
CREATE TABLE permission (
  code           text PRIMARY KEY,                           -- module.action, e.g. 'bet.void'
  module_code    text NOT NULL,
  description    text NOT NULL,
  risk_level     text NOT NULL DEFAULT 'normal' CHECK (risk_level IN ('normal', 'sensitive', 'critical')),
  platform_only  boolean NOT NULL DEFAULT false
);

CREATE TABLE role (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id  bigint REFERENCES operator(id),               -- NULL = system role
  code         text NOT NULL,
  name         text NOT NULL,
  is_system    boolean NOT NULL DEFAULT false,
  is_platform  boolean NOT NULL DEFAULT false,               -- assignable to platform staff only
  version      integer NOT NULL DEFAULT 1,
  UNIQUE NULLS NOT DISTINCT (operator_id, code)
);

CREATE TABLE role_permission (
  role_id          bigint NOT NULL REFERENCES role(id) ON DELETE CASCADE,
  permission_code  text NOT NULL REFERENCES permission(code),
  constraints      jsonb,                                    -- e.g. {"max_stake_base": 500}
  PRIMARY KEY (role_id, permission_code)
);

CREATE TABLE user_role (
  user_id     uuid NOT NULL REFERENCES admin_user(id) ON DELETE CASCADE,
  role_id     bigint NOT NULL REFERENCES role(id),
  granted_by  uuid,
  granted_at  timestamptz NOT NULL DEFAULT now(),
  expires_at  timestamptz,
  PRIMARY KEY (user_id, role_id)
);

-- =====================================================================
-- Audit (append-only) and transactional outbox
-- =====================================================================
CREATE TABLE audit_log (
  id           bigint GENERATED ALWAYS AS IDENTITY,
  ts           timestamptz NOT NULL DEFAULT now(),
  operator_id  bigint,                                       -- NULL = platform-level action
  actor_id     uuid,
  actor_type   text NOT NULL CHECK (actor_type IN ('bo_user', 'platform_user', 'system', 'api_client')),
  actor_name   text,
  action       text NOT NULL,                                -- 'cfg.change_set.applied', 'adm.user.roles_changed'
  entity_type  text NOT NULL,
  entity_id    text NOT NULL,
  before       jsonb,
  after        jsonb,
  reason       text,
  request_id   text,
  PRIMARY KEY (ts, id)
) PARTITION BY RANGE (ts);
CREATE TABLE audit_log_default PARTITION OF audit_log DEFAULT;   -- monthly partitions later (pg_partman)
CREATE INDEX audit_log_entity_idx ON audit_log (operator_id, entity_type, entity_id, ts DESC);
CREATE INDEX audit_log_actor_idx  ON audit_log (operator_id, actor_id, ts DESC);
CREATE INDEX audit_log_ts_idx     ON audit_log (operator_id, ts DESC, id DESC);

CREATE TABLE outbox (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id   bigint,
  topic         text NOT NULL,                               -- NATS subject, e.g. 'bo.changed.42.cfg'
  payload       jsonb NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  published_at  timestamptz
);
CREATE INDEX outbox_pending_idx ON outbox (id) WHERE published_at IS NULL;

-- =====================================================================
-- CFG: hierarchical settings (docs/06 §5, docs/09 §2.1-2.3)
-- Scope chain: platform → operator → brand → sport → category → tournament → event → market;
-- market_type_id is an optional qualifier on any level.
-- =====================================================================
CREATE TABLE setting_def (
  key                   text PRIMARY KEY,
  module                text NOT NULL,
  value_type            text NOT NULL CHECK (value_type IN ('bool', 'int', 'decimal', 'string', 'enum', 'money', 'json', 'string_list')),
  enum_values           text[],
  min_value             numeric,
  max_value             numeric,
  allowed_scopes        text[] NOT NULL,
  allows_market_type    boolean NOT NULL DEFAULT false,
  default_value         jsonb,
  combine               text NOT NULL DEFAULT 'override' CHECK (combine IN ('override', 'all_path', 'min_path', 'max_path')),
  customer_combine      text NOT NULL DEFAULT 'none' CHECK (customer_combine IN ('none', 'min', 'max', 'override', 'multiply')),
  is_operator_editable  boolean NOT NULL DEFAULT true,
  requires_approval     boolean NOT NULL DEFAULT false,
  description           text NOT NULL
);

CREATE TABLE setting_change_set (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id       bigint REFERENCES operator(id),          -- NULL = platform settings
  title             text NOT NULL,
  status            text NOT NULL CHECK (status IN ('pending_approval', 'applied', 'rejected', 'cancelled')),
  created_by        uuid NOT NULL,
  created_by_name   text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  decided_by        uuid,
  decided_by_name   text,
  decided_at        timestamptz,
  decision_comment  text,
  applied_at        timestamptz,
  config_version    bigint,                                  -- version produced by applying it
  CHECK (decided_by IS NULL OR decided_by <> created_by)    -- four-eyes
);
CREATE INDEX setting_change_set_status_idx ON setting_change_set (operator_id, status, id DESC);

CREATE TABLE setting (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id     bigint REFERENCES operator(id),            -- NULL = platform scope
  scope_type      text NOT NULL CHECK (scope_type IN ('platform', 'operator', 'brand', 'sport', 'category', 'tournament', 'event', 'market')),
  scope_id        bigint,                                    -- NULL for platform/operator, else the brand/sb row id
  market_type_id  integer,                                   -- qualifier (sb.market_description.id)
  key             text NOT NULL REFERENCES setting_def(key),
  value           jsonb NOT NULL,
  change_set_id   bigint NOT NULL REFERENCES setting_change_set(id),
  version         integer NOT NULL DEFAULT 1,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  CHECK ((scope_type = 'platform') = (operator_id IS NULL)),
  CHECK ((scope_type IN ('platform', 'operator')) = (scope_id IS NULL)),
  UNIQUE NULLS NOT DISTINCT (operator_id, scope_type, scope_id, market_type_id, key)
);
CREATE INDEX setting_operator_key_idx ON setting (operator_id, key);

CREATE TABLE setting_change (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  change_set_id   bigint NOT NULL REFERENCES setting_change_set(id),
  op              text NOT NULL CHECK (op IN ('upsert', 'delete')),
  scope_type      text NOT NULL,
  scope_id        bigint,
  market_type_id  integer,
  key             text NOT NULL,
  old_value       jsonb,
  new_value       jsonb
);
CREATE INDEX setting_change_set_idx ON setting_change (change_set_id);
CREATE INDEX setting_change_key_idx ON setting_change (key, scope_type, scope_id);

-- Monotonic per tenant (NULL row = platform); every applied change set bumps it (docs/06 §5.4).
CREATE TABLE config_version (
  operator_id  bigint REFERENCES operator(id),
  version      bigint NOT NULL DEFAULT 0,
  UNIQUE NULLS NOT DISTINCT (operator_id)
);
INSERT INTO config_version (operator_id, version) VALUES (NULL, 0);

-- =====================================================================
-- Row-level security
-- =====================================================================
ALTER TABLE operator ENABLE ROW LEVEL SECURITY;
CREATE POLICY operator_read ON operator FOR SELECT USING (id = bo.current_operator() OR bo.is_platform());
CREATE POLICY operator_write ON operator FOR ALL USING (bo.is_platform()) WITH CHECK (bo.is_platform());

-- Strictly per tenant: rows of the operator in the request context only.
ALTER TABLE brand ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON brand USING (operator_id = bo.current_operator()) WITH CHECK (operator_id = bo.current_operator());
ALTER TABLE operator_module ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON operator_module USING (operator_id = bo.current_operator()) WITH CHECK (operator_id = bo.current_operator());

-- Tenant rows, plus platform-level rows (operator_id NULL) for platform staff.
ALTER TABLE admin_user ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON admin_user
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON audit_log
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE outbox ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON outbox
  USING (false)
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

-- Readable: platform rows (NULL) + own tenant rows. Writable: own tenant rows, platform rows by platform staff.
ALTER TABLE role ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON role FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON role FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE role_permission ENABLE ROW LEVEL SECURITY;
CREATE POLICY via_role ON role_permission USING (EXISTS (SELECT 1 FROM bo.role r WHERE r.id = role_id));
ALTER TABLE user_role ENABLE ROW LEVEL SECURITY;
CREATE POLICY via_user ON user_role USING (EXISTS (SELECT 1 FROM bo.admin_user u WHERE u.id = user_id))
  WITH CHECK (EXISTS (SELECT 1 FROM bo.admin_user u WHERE u.id = user_id));

ALTER TABLE setting ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON setting FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON setting FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE setting_change_set ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON setting_change_set FOR SELECT USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
CREATE POLICY write ON setting_change_set FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE setting_change ENABLE ROW LEVEL SECURITY;
CREATE POLICY via_change_set ON setting_change USING (EXISTS (SELECT 1 FROM bo.setting_change_set c WHERE c.id = change_set_id))
  WITH CHECK (EXISTS (SELECT 1 FROM bo.setting_change_set c WHERE c.id = change_set_id));
ALTER TABLE config_version ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON config_version FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON config_version FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

-- =====================================================================
-- Privileges for the application role
-- =====================================================================
GRANT USAGE ON SCHEMA bo, sb TO bo_app;
GRANT SELECT ON ALL TABLES IN SCHEMA sb TO bo_app;
GRANT SELECT ON permission, setting_def TO bo_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON operator, brand, operator_module, admin_user, role, role_permission, user_role,
  setting, setting_change_set, setting_change, config_version TO bo_app;
GRANT SELECT, INSERT ON audit_log TO bo_app;                 -- append-only: no UPDATE / DELETE
GRANT INSERT ON outbox TO bo_app;                            -- the relay reads it outside the tenant role
GRANT USAGE ON ALL SEQUENCES IN SCHEMA bo TO bo_app;
