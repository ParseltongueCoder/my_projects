-- V007: BO-1b trading and messages: odds overrides, suspend / close, manual markets, reason-code messages.
-- Design: docs/06 §4 (ODDS), §6 (CMS), §8; binding decisions docs/09 §2.4 (BO writes sb only for manual entities),
-- §2.5 (RLS), §2.8-2.9 (one price pipeline, manual odds), §2.16 (brand dimension for texts).
-- Prices themselves are computed by Offer.Core at read time from sb + these overlays; nothing here stores offer odds.

SET search_path = bo;

-- Provider of everything the back office creates in sb (docs/06 §1.1).
INSERT INTO sb.provider (id, code, name) VALUES (0, 'manual', 'Manual (back office)') ON CONFLICT (id) DO NOTHING;

-- Who owns a manual sb row. An operator's manual market is invisible to every other operator.
CREATE TABLE manual_entity (
  entity_type  text   NOT NULL CHECK (entity_type IN ('event', 'market')),
  entity_id    bigint NOT NULL,                              -- sb.event.id / sb.market.id
  operator_id  bigint REFERENCES operator(id),               -- NULL = platform (every operator)
  state        text   NOT NULL DEFAULT 'published' CHECK (state IN ('draft', 'published', 'settled', 'cancelled')),
  meta         jsonb  NOT NULL DEFAULT '{}'::jsonb,          -- {"fromMarketTypeId": 1}
  created_by   uuid,
  created_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1,
  PRIMARY KEY (entity_type, entity_id)
);
CREATE INDEX manual_entity_operator_idx ON manual_entity (operator_id, entity_type);

CREATE FUNCTION bo.owns_manual(p_type text, p_id bigint) RETURNS boolean LANGUAGE sql STABLE
  AS $$ SELECT EXISTS (SELECT 1 FROM bo.manual_entity me WHERE me.entity_type = p_type AND me.entity_id = p_id
                         AND (me.operator_id = bo.current_operator() OR (me.operator_id IS NULL AND bo.is_platform()))) $$;

-- Manual price on one outcome (docs/06 §4.3). A TTL is mandatory; clear_on='feed_change' also ends it once the
-- feed price moved past odds.override_feed_tolerance_pct. One active override per outcome.
CREATE TABLE odds_override (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id       bigint NOT NULL REFERENCES operator(id),
  market_id         bigint NOT NULL,                         -- sb.market.id
  outcome_code      text   NOT NULL,
  kind              text   NOT NULL CHECK (kind IN ('absolute', 'shift_pct')),
  value             numeric(10,4) NOT NULL,                  -- odds (absolute) or -0.05 (shift)
  clear_on          text   NOT NULL DEFAULT 'expiry' CHECK (clear_on IN ('expiry', 'feed_change')),
  feed_odds_at_set  numeric(10,3),
  expires_at        timestamptz NOT NULL,
  reason            text   NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
  created_by        uuid,
  created_by_name   text,
  created_at        timestamptz NOT NULL DEFAULT now(),
  cleared_at        timestamptz,
  cleared_by        uuid,
  cleared_by_name   text,                                    -- user, or 'system:expiry' | 'system:feed_change' | 'system:settled'
  clear_reason      text,
  CHECK (expires_at > created_at)
);
CREATE UNIQUE INDEX odds_override_active_uq ON odds_override (operator_id, market_id, outcome_code) WHERE cleared_at IS NULL;
CREATE INDEX odds_override_market_idx ON odds_override (market_id) WHERE cleared_at IS NULL;
CREATE INDEX odds_override_expiry_idx ON odds_override (expires_at) WHERE cleared_at IS NULL;

-- Suspend or close an event / market for one operator, or for all of them (operator_id NULL: our incident response).
-- price_lock (docs/06 §4.4) is P2.
CREATE TABLE trading_override (
  id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id      bigint REFERENCES operator(id),
  scope_type       text   NOT NULL CHECK (scope_type IN ('event', 'market')),
  scope_id         bigint NOT NULL,
  action           text   NOT NULL CHECK (action IN ('suspend', 'close')),
  expires_at       timestamptz,                              -- NULL = until cleared by hand
  reason           text   NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
  created_by       uuid,
  created_by_name  text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  cleared_at       timestamptz,
  cleared_by       uuid,
  cleared_by_name  text,
  clear_reason     text
);
CREATE UNIQUE INDEX trading_override_active_uq ON trading_override (operator_id, scope_type, scope_id, action) NULLS NOT DISTINCT
  WHERE cleared_at IS NULL;
CREATE INDEX trading_override_scope_idx ON trading_override (scope_type, scope_id) WHERE cleared_at IS NULL;

-- Reason codes the player can get back (docs/06 §6). Code is the source of truth (Bo.Core MessageCatalog), synced at
-- startup. Texts: bo.translation entity_type 'message' (brand → operator → platform), then these defaults.
CREATE TABLE message_def (
  code                 text PRIMARY KEY CHECK (code ~ '^[A-Z][A-Z0-9_]{2,63}$'),
  module               text NOT NULL,
  category             text NOT NULL CHECK (category IN ('bet_reject', 'cashout', 'referral', 'validation', 'system', 'info')),
  severity             text NOT NULL CHECK (severity IN ('info', 'warning', 'error')),
  params               text[] NOT NULL DEFAULT '{}',
  defaults             jsonb NOT NULL,                       -- {"en":{"title":"...","text":"..."},"ka":{...}}
  is_customer_visible  boolean NOT NULL DEFAULT true,
  description          text NOT NULL
);

-- =====================================================================
-- Row-level security (same pattern as V005/V006)
-- =====================================================================
ALTER TABLE manual_entity ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON manual_entity FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON manual_entity FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

ALTER TABLE odds_override ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON odds_override USING (operator_id = bo.current_operator()) WITH CHECK (operator_id = bo.current_operator());

ALTER TABLE trading_override ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON trading_override FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON trading_override FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

-- sb stays feed truth: as bo_app the back office may only create manual market types (code 'manual:*') and create /
-- change markets and outcomes it owns (source_producer_id 0 + bo.manual_entity). Other roles (adapter, Feed Ops,
-- migrations) are not restricted by these policies.
ALTER TABLE sb.market ENABLE ROW LEVEL SECURITY;
CREATE POLICY not_bo ON sb.market USING (current_user <> 'bo_app') WITH CHECK (current_user <> 'bo_app');
CREATE POLICY bo_read ON sb.market FOR SELECT TO bo_app USING (true);
CREATE POLICY bo_manual_insert ON sb.market FOR INSERT TO bo_app WITH CHECK (source_producer_id = 0);
CREATE POLICY bo_manual_update ON sb.market FOR UPDATE TO bo_app
  USING (source_producer_id = 0 AND bo.owns_manual('market', id)) WITH CHECK (source_producer_id = 0);

ALTER TABLE sb.outcome ENABLE ROW LEVEL SECURITY;
CREATE POLICY not_bo ON sb.outcome USING (current_user <> 'bo_app') WITH CHECK (current_user <> 'bo_app');
CREATE POLICY bo_read ON sb.outcome FOR SELECT TO bo_app USING (true);
CREATE POLICY bo_manual_insert ON sb.outcome FOR INSERT TO bo_app WITH CHECK (bo.owns_manual('market', market_id));
CREATE POLICY bo_manual_update ON sb.outcome FOR UPDATE TO bo_app
  USING (bo.owns_manual('market', market_id)) WITH CHECK (bo.owns_manual('market', market_id));

ALTER TABLE sb.market_description ENABLE ROW LEVEL SECURITY;
CREATE POLICY not_bo ON sb.market_description USING (current_user <> 'bo_app') WITH CHECK (current_user <> 'bo_app');
CREATE POLICY bo_read ON sb.market_description FOR SELECT TO bo_app USING (true);
CREATE POLICY bo_manual_insert ON sb.market_description FOR INSERT TO bo_app WITH CHECK (code LIKE 'manual:%');

ALTER TABLE sb.market_description_outcome ENABLE ROW LEVEL SECURITY;
CREATE POLICY not_bo ON sb.market_description_outcome USING (current_user <> 'bo_app') WITH CHECK (current_user <> 'bo_app');
CREATE POLICY bo_read ON sb.market_description_outcome FOR SELECT TO bo_app USING (true);
CREATE POLICY bo_manual_insert ON sb.market_description_outcome FOR INSERT TO bo_app WITH CHECK (EXISTS (
  SELECT 1 FROM sb.market_description d WHERE d.id = market_description_id AND d.code LIKE 'manual:%'));

ALTER TABLE sb.market_specifier_def ENABLE ROW LEVEL SECURITY;
CREATE POLICY not_bo ON sb.market_specifier_def USING (current_user <> 'bo_app') WITH CHECK (current_user <> 'bo_app');
CREATE POLICY bo_read ON sb.market_specifier_def FOR SELECT TO bo_app USING (true);
CREATE POLICY bo_manual_insert ON sb.market_specifier_def FOR INSERT TO bo_app WITH CHECK (EXISTS (
  SELECT 1 FROM sb.market_description d WHERE d.id = market_description_id AND d.code LIKE 'manual:%'));

GRANT SELECT ON message_def TO bo_app;
GRANT SELECT, INSERT, UPDATE ON manual_entity, odds_override, trading_override TO bo_app;
GRANT INSERT, UPDATE ON sb.market, sb.outcome TO bo_app;
GRANT INSERT ON sb.market_description, sb.market_description_outcome, sb.market_specifier_def TO bo_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA bo TO bo_app;
