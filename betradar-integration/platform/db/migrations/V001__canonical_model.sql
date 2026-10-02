-- V001: canonical model, copied verbatim from docs/02-uof-data-model.md §6.2.
-- Change the schema through new migrations, not by editing this file.

CREATE SCHEMA IF NOT EXISTS sb;
SET search_path = sb;

-- =====================================================================
-- ENUM-ები
-- =====================================================================
CREATE TYPE event_type          AS ENUM ('match', 'stage', 'outright', 'draw');
CREATE TYPE event_status        AS ENUM ('not_started', 'live', 'suspended', 'ended', 'closed',
                                         'cancelled', 'delayed', 'interrupted', 'postponed', 'abandoned');
CREATE TYPE competitor_qualifier AS ENUM ('home', 'away');
-- handed_over (-2) აქ განზრახ არ არის: ეს status არ არის, ეს ownership-ის ცვლილებაა (იხ. §7.1)
CREATE TYPE market_status       AS ENUM ('active', 'suspended', 'deactivated', 'settled', 'cancelled');
CREATE TYPE outcome_result      AS ENUM ('lost', 'won', 'undecided');
CREATE TYPE specifier_type      AS ENUM ('integer', 'decimal', 'string', 'variable_text', 'competitor', 'player');
CREATE TYPE outcome_kind        AS ENUM ('static', 'variant', 'player', 'competitor', 'free_text');
CREATE TYPE producer_state      AS ENUM ('up', 'down', 'recovering');
CREATE TYPE feed_msg_status     AS ENUM ('received', 'processed', 'queued', 'skipped_stale',
                                         'skipped_duplicate', 'failed');
CREATE TYPE rollback_kind       AS ENUM ('settlement', 'cancellation');

-- =====================================================================
-- Provider-ები და mapping
-- =====================================================================
CREATE TABLE provider (
  id          smallint PRIMARY KEY,
  code        text NOT NULL UNIQUE,          -- 'betradar_uof'
  name        text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO provider (id, code, name) VALUES (1, 'betradar_uof', 'Betradar Unified Odds Feed');

-- გარე ID → შიდა ID. ერთი ცხრილი ყველა entity-სთვის.
CREATE TABLE provider_mapping (
  provider_id         smallint NOT NULL REFERENCES provider(id),
  entity_type         text     NOT NULL CHECK (entity_type IN (
                        'sport','category','tournament','season','competitor','player',
                        'event','market_type','market_outcome','venue')),
  provider_entity_id  text     NOT NULL,     -- 'sr:match:61000001', 'sr:competitor:5000001', '18' (market), '18:12' (outcome)
  internal_id         bigint   NOT NULL,
  meta                jsonb,                 -- მაგ. {"reference_ids":{"BetradarCtrl":"11259634"}}
  created_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (provider_id, entity_type, provider_entity_id)
);
-- reverse lookup (internal → external), მაგ. recovery-სთვის, როცა URN გვჭირდება
CREATE INDEX provider_mapping_reverse_idx ON provider_mapping (entity_type, internal_id, provider_id);

-- =====================================================================
-- სპორტული იერარქია
-- =====================================================================
CREATE TABLE sport (
  id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  code        text NOT NULL UNIQUE,                 -- 'soccer'
  name_i18n   jsonb NOT NULL,                       -- {"en":"Soccer","ka":"ფეხბურთი"}
  sort_order  integer,
  is_enabled  boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE category (
  id            integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  sport_id      integer NOT NULL REFERENCES sport(id),
  name_i18n     jsonb   NOT NULL,
  country_code  char(3),                            -- ISO-3 (UOF: country_code="SWE")
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX category_sport_idx ON category (sport_id);

CREATE TABLE tournament (
  id                 integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  sport_id           integer NOT NULL REFERENCES sport(id),
  category_id        integer NOT NULL REFERENCES category(id),
  name_i18n          jsonb   NOT NULL,
  current_season_id  integer,                       -- FK ქვემოთ (წრიული დამოკიდებულება)
  is_enabled         boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX tournament_category_idx ON tournament (category_id);
CREATE INDEX tournament_sport_idx    ON tournament (sport_id);

CREATE TABLE season (
  id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tournament_id  integer NOT NULL REFERENCES tournament(id),
  name           text NOT NULL,                     -- 'Example League 26/27'
  year           text,                              -- '2016' / '16/17'
  start_date     date,
  end_date       date,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX season_tournament_idx ON season (tournament_id);
ALTER TABLE tournament ADD CONSTRAINT tournament_current_season_fk
  FOREIGN KEY (current_season_id) REFERENCES season(id);

CREATE TABLE competitor (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  sport_id      integer REFERENCES sport(id),
  name_i18n     jsonb NOT NULL,
  abbreviation  text,
  country_code  char(3),
  gender        text,
  is_virtual    boolean NOT NULL DEFAULT false,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE player (                               -- goalscorer / player props outcome-ებისთვის
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  competitor_id  bigint REFERENCES competitor(id),
  name_i18n      jsonb NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now()
);

-- =====================================================================
-- Event
-- =====================================================================
CREATE TABLE event (
  id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_type            event_type   NOT NULL,
  sport_id              integer      NOT NULL REFERENCES sport(id),
  tournament_id         integer      REFERENCES tournament(id),
  season_id             integer      REFERENCES season(id),
  parent_event_id       bigint       REFERENCES event(id),      -- sr:stage იერარქია
  name_i18n             jsonb,                                  -- stage/outright; match-ის სახელი competitor-ებიდან გამოითვლება
  scheduled_at          timestamptz,
  start_time_confirmed  boolean,
  next_live_time        timestamptz,                            -- fixture_change@next_live_time
  live_odds_availability text,                                  -- fixture@liveodds: 'booked'|'bookable'|'not_available'
  status                event_status NOT NULL DEFAULT 'not_started',
  match_status_code     integer,                                -- UOF match_status (6 = 1st half ...)
  reporting_status      smallint,                               -- 1 / 0 / -1
  home_score            numeric(8,2),
  away_score            numeric(8,2),
  clock                 jsonb,                                  -- {"match_time":"27:33","stopped":false}
  period_scores         jsonb,                                  -- [{"number":1,"code":6,"home":0,"away":2}]
  statistics            jsonb,                                  -- cards, corners
  last_feed_ts          timestamptz,                            -- ბოლოს გამოყენებული sport_event_status-ის timestamp
  version               integer      NOT NULL DEFAULT 0,        -- optimistic locking / downstream ordering
  created_at            timestamptz  NOT NULL DEFAULT now(),
  updated_at            timestamptz  NOT NULL DEFAULT now()
);
-- lobby/coupon query: "სპორტი X, მომავალი 3 დღე"
CREATE INDEX event_sport_sched_idx      ON event (sport_id, scheduled_at)
  WHERE status IN ('not_started','live','delayed','interrupted','suspended');
CREATE INDEX event_tournament_sched_idx ON event (tournament_id, scheduled_at);
-- live lobby
CREATE INDEX event_live_idx             ON event (sport_id) WHERE status = 'live';
CREATE INDEX event_parent_idx           ON event (parent_event_id) WHERE parent_event_id IS NOT NULL;

CREATE TABLE event_competitor (
  event_id       bigint   NOT NULL REFERENCES event(id) ON DELETE CASCADE,
  position       smallint NOT NULL CHECK (position >= 1),  -- 1 = {$competitor1}, 2 = {$competitor2}
  competitor_id  bigint   NOT NULL REFERENCES competitor(id),
  qualifier      competitor_qualifier,                     -- home/away (NULL neutral/racing-ზე)
  PRIMARY KEY (event_id, position)
);
CREATE INDEX event_competitor_comp_idx ON event_competitor (competitor_id);

-- =====================================================================
-- Market catalogue
-- =====================================================================
CREATE TABLE market_description (
  id                  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  code                text  NOT NULL UNIQUE,         -- 'total', '1x2', ან 'uof_18' seed-ის დროს
  name_template_i18n  jsonb NOT NULL,                -- {"en":"Total","ka":"ტოტალი"}
  groups              text[] NOT NULL DEFAULT '{}',  -- {'all','score','regular_play'} - bet_stop-ისთვის
  outcome_kind        outcome_kind NOT NULL DEFAULT 'static',
  is_variant          boolean NOT NULL DEFAULT false,-- აქვს 'variant' specifier
  attributes          jsonb NOT NULL DEFAULT '{}',   -- {"is_flex_score":true,"is_spread_market":false}
  is_deprecated       boolean NOT NULL DEFAULT false,
  is_enabled          boolean NOT NULL DEFAULT true, -- ჩვენი trading-ის გადაწყვეტილება
  provider_raw        jsonb,                         -- mappings და სხვ. (debug/legacy)
  source_hash         text,                          -- provider XML-ის hash ცვლილებების აღმოსაჩენად
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now()
);
-- bet_stop groups="score|1st_half" → WHERE groups && '{score,1st_half}'
CREATE INDEX market_description_groups_gin ON market_description USING gin (groups);

CREATE TABLE market_specifier_def (
  market_description_id  integer       NOT NULL REFERENCES market_description(id) ON DELETE CASCADE,
  name                   text          NOT NULL,          -- 'total', 'hcp', 'variant'
  type                   specifier_type NOT NULL,
  description            text,
  ordinal                smallint      NOT NULL DEFAULT 0,
  PRIMARY KEY (market_description_id, name)
);

CREATE TABLE market_description_outcome (
  id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  market_description_id  integer NOT NULL REFERENCES market_description(id) ON DELETE CASCADE,
  variant                text    NOT NULL DEFAULT '',     -- '' სტატიკურისთვის; 'sr:exact_goals:4+' variant-ისთვის
  code                   text    NOT NULL,                -- '12', 'sr:exact_goals:4+:92'
  name_template_i18n     jsonb   NOT NULL,                -- {"en":"over {total}"}
  ordinal                smallint NOT NULL DEFAULT 0,
  UNIQUE (market_description_id, variant, code)
);

-- =====================================================================
-- Market / Outcome (live state)
-- =====================================================================
CREATE TABLE market (
  id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id               bigint  NOT NULL REFERENCES event(id) ON DELETE CASCADE,
  market_description_id  integer NOT NULL REFERENCES market_description(id),
  specifiers             text    NOT NULL DEFAULT '',     -- ნორმალიზებული: sorted 'k=v|k=v'
  specifiers_json        jsonb   NOT NULL DEFAULT '{}',   -- {"total":"2.5"} - name rendering-ისთვის
  extended_specifiers    jsonb,                           -- იდენტობაში არ შედის
  variant                text GENERATED ALWAYS AS (specifiers_json->>'variant') STORED,
  status                 market_status NOT NULL,
  status_before_close    market_status,                   -- settle/cancel-მდე არსებული status (rollback-ისთვის)
  source_producer_id     smallint,                        -- ვინ "ფლობს" market-ს ახლა (1=LO, 3=Ctrl)
  is_favourite           boolean NOT NULL DEFAULT false,  -- favourite="1" (მთავარი ხაზი)
  cashout_status         smallint,
  next_betstop_at        timestamptz,                     -- market_metadata@next_betstop
  void_reason            integer,
  last_feed_ts           timestamptz NOT NULL,            -- stale-check
  settled_at             timestamptz,
  cancelled_at           timestamptz,
  version                integer NOT NULL DEFAULT 0,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT market_nk UNIQUE (event_id, market_description_id, specifiers)
);
-- market_nk ფარავს "ყველა market event-ზე" query-ს (leftmost prefix event_id).
-- front-end-ს მხოლოდ აქტიური market-ები სჭირდება:
CREATE INDEX market_event_open_idx ON market (event_id) WHERE status IN ('active','suspended');
-- producer down → "suspend ყველაფერი, რასაც producer X ფლობს"
CREATE INDEX market_producer_open_idx ON market (source_producer_id) WHERE status IN ('active','suspended');
CREATE INDEX market_desc_idx ON market (market_description_id);

CREATE TABLE outcome (
  market_id               bigint  NOT NULL REFERENCES market(id) ON DELETE CASCADE,
  code                    text    NOT NULL,       -- '12' | 'sr:exact_goals:4+:92' | 'player:{id}' | 'competitor:{id}' | 'text:{provider_id}'
  description_outcome_id  bigint  REFERENCES market_description_outcome(id),
  player_id               bigint  REFERENCES player(id),
  competitor_id           bigint  REFERENCES competitor(id),
  name_i18n               jsonb,                  -- dynamic outcome-ის სახელი (free text / player props)
  odds                    numeric(10,3),          -- NULL = odds არ არის (active=0 ხშირად odds-ის გარეშე მოდის)
  probability             numeric(14,12),
  is_active               boolean NOT NULL DEFAULT true,
  team                    smallint,               -- outcome@team (1/2)
  -- settlement-ის მიმდინარე (effective) მდგომარეობა; ისტორია settlement ცხრილშია
  result                  outcome_result,
  void_factor             numeric(3,2) CHECK (void_factor IN (0.5, 1.0)),
  dead_heat_factor        numeric(12,10),
  settlement_certainty    smallint CHECK (settlement_certainty IN (1, 2)),
  odds_updated_at         timestamptz,
  settled_at              timestamptz,
  PRIMARY KEY (market_id, code)
);
CREATE INDEX outcome_player_idx ON outcome (player_id) WHERE player_id IS NOT NULL;

-- (არასავალდებულო, Phase 1.5) odds-ის ისტორია analytics/monitoring-ისთვის, partitioned დღეების მიხედვით.
-- CREATE TABLE outcome_odds_history (market_id bigint, code text, odds numeric(10,3), probability numeric(14,12),
--   is_active boolean, feed_ts timestamptz NOT NULL, producer_id smallint) PARTITION BY RANGE (feed_ts);

-- =====================================================================
-- Bet stop / Settlement / Cancel / Rollback (append-only)
-- =====================================================================
CREATE TABLE bet_stop_log (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id          bigint   NOT NULL REFERENCES event(id),
  producer_id       smallint NOT NULL,
  source            text     NOT NULL CHECK (source IN ('bet_stop','odds_change','producer_down')),
  groups            text[],                         -- {'all'} ან {'score','1st_half'}
  target_status     market_status NOT NULL,         -- suspended / deactivated
  betstop_reason    integer,
  betting_status    integer,
  affected_markets  integer,
  feed_ts           timestamptz NOT NULL,
  feed_message_id   bigint,                         -- → feed_message_log.id (FK არ ადევს: partitioned)
  created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX bet_stop_log_event_idx ON bet_stop_log (event_id, feed_ts DESC);

CREATE TABLE rollback (
  id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  kind             rollback_kind NOT NULL,
  event_id         bigint   NOT NULL REFERENCES event(id),
  market_id        bigint   NOT NULL REFERENCES market(id),
  producer_id      smallint NOT NULL,
  start_time       timestamptz,                     -- rollback_bet_cancel-ისთვის
  end_time         timestamptz,
  feed_ts          timestamptz NOT NULL,
  feed_message_id  bigint,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX rollback_market_idx ON rollback (market_id);

CREATE TABLE settlement (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id          bigint   NOT NULL REFERENCES event(id),
  market_id         bigint   NOT NULL REFERENCES market(id),
  outcome_code      text     NOT NULL,
  result            outcome_result NOT NULL,
  void_factor       numeric(3,2) CHECK (void_factor IN (0.5, 1.0)),
  dead_heat_factor  numeric(12,10),
  certainty         smallint NOT NULL CHECK (certainty IN (1, 2)),
  void_reason       integer,
  producer_id       smallint NOT NULL,
  feed_ts           timestamptz NOT NULL,
  feed_message_id   bigint,
  superseded_by_id  bigint REFERENCES settlement(id),   -- certainty upgrade / resettlement
  rolled_back_at    timestamptz,
  rollback_id       bigint REFERENCES rollback(id),
  created_at        timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (market_id, outcome_code) REFERENCES outcome(market_id, code)
);
-- ერთ outcome-ზე მხოლოდ ერთი "effective" settlement. ორმაგი გადახდის დაცვა DB დონეზე:
CREATE UNIQUE INDEX settlement_effective_uq ON settlement (market_id, outcome_code)
  WHERE rolled_back_at IS NULL AND superseded_by_id IS NULL;
CREATE INDEX settlement_event_idx ON settlement (event_id);
CREATE INDEX settlement_msg_idx   ON settlement (feed_message_id);

CREATE TABLE market_cancellation (
  id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id              bigint   NOT NULL REFERENCES event(id),
  market_id             bigint   NOT NULL REFERENCES market(id),
  void_reason           integer,
  start_time            timestamptz,               -- NULL = დასაწყისიდან
  end_time              timestamptz,               -- NULL = დღემდე
  superceded_by_urn     text,                      -- bet_cancel@superceded_by (outright → ახალი season)
  producer_id           smallint NOT NULL,
  feed_ts               timestamptz NOT NULL,
  feed_message_id       bigint,
  rolled_back_at        timestamptz,
  rollback_id           bigint REFERENCES rollback(id),
  created_at            timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX market_cancellation_market_idx ON market_cancellation (market_id) WHERE rolled_back_at IS NULL;
CREATE INDEX market_cancellation_event_idx  ON market_cancellation (event_id);

-- =====================================================================
-- Feed audit / replay
-- =====================================================================
-- identity column partitioned ცხრილზე მხოლოდ PG17+-ში მუშაობს, ამიტომ PG15/16-ისთვის ვიყენებთ sequence-ს
CREATE SEQUENCE feed_message_log_id_seq AS bigint;
CREATE TABLE feed_message_log (
  id               bigint NOT NULL DEFAULT nextval('feed_message_log_id_seq'),
  received_at      timestamptz NOT NULL DEFAULT now(),
  provider_id      smallint NOT NULL,
  producer_id      smallint,
  message_type     text     NOT NULL,             -- 'odds_change', 'bet_settlement', ...
  routing_key      text,
  event_urn        text,                          -- 'sr:match:61000001' (raw, mapping-მდე)
  sport_ref        text,                          -- routing key-ის sport segment
  request_id       bigint,
  feed_ts          timestamptz,                   -- XML @timestamp (generation)
  sent_ts          timestamptz,                   -- AMQP header timestamp_in_ms
  payload          text COMPRESSION lz4 NOT NULL, -- raw XML
  payload_sha256   bytea    NOT NULL,
  status           feed_msg_status NOT NULL DEFAULT 'received',
  error            text,
  processed_at     timestamptz,
  PRIMARY KEY (id, received_at)
) PARTITION BY RANGE (received_at);
-- დღიური partition-ები (pg_partman); retention: hot 30 დღე, შემდეგ archive (S3/parquet)
CREATE INDEX feed_log_event_idx ON feed_message_log (event_urn, feed_ts);
CREATE INDEX feed_log_type_idx  ON feed_message_log (message_type, received_at);
CREATE INDEX feed_log_req_idx   ON feed_message_log (request_id) WHERE request_id IS NOT NULL;
CREATE INDEX feed_log_fail_idx  ON feed_message_log (received_at) WHERE status = 'failed';
-- dedup: AMQP at-least-once + recovery overlap. ერთ partition-ში (დღეში) ერთი hash:
CREATE UNIQUE INDEX feed_log_dedup_uq ON feed_message_log (payload_sha256, received_at);
-- ⚠ ზემოთ მოცემული unique index received_at-ის გამო ფაქტობრივად dedup-ს ვერ უზრუნველყოფს.
--   რეალური dedup ხდება app-ში (Redis SETNX sha256, TTL 1h) ან (payload_sha256, received_at::date)-ით,
--   თუ partition key date ტიპის სვეტზე გადავა. ეს გადაწყვეტილება implementation-ის ეტაპზე უნდა მივიღოთ.

-- =====================================================================
-- Producer status / Recovery
-- =====================================================================
CREATE TABLE producer_status (
  provider_id               smallint NOT NULL REFERENCES provider(id),
  producer_id               smallint NOT NULL,          -- 1 LO, 3 Ctrl ...
  name                      text NOT NULL,              -- 'LO'
  scope                     text[] NOT NULL DEFAULT '{}', -- {'live'} / {'prematch'}
  api_url                   text,                       -- 'https://api.betradar.com/v1/liveodds/'
  is_enabled                boolean NOT NULL DEFAULT true, -- გვაქვს თუ არა ეს პროდუქტი კონტრაქტში
  state                     producer_state NOT NULL DEFAULT 'down',
  down_reason               text,                       -- 'alive_timeout','connection_down','subscribed_0','processing_delay'
  last_alive_at             timestamptz,                -- alive@timestamp
  last_alive_received_at    timestamptz,                -- ლოკალური დრო
  last_alive_subscribed     boolean,
  last_processed_feed_ts    timestamptz,                -- recovery-ის 'after'
  recovery_window_minutes   integer NOT NULL DEFAULT 4320, -- producers.xml-დან
  current_request_id        bigint,
  recovery_started_at       timestamptz,
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (provider_id, producer_id)
);

CREATE TABLE recovery_request (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  provider_id        smallint NOT NULL,
  producer_id        smallint NOT NULL,
  request_id         bigint   NOT NULL,                 -- ჩვენ მიერ გენერირებული, feed-ში ბრუნდება
  kind               text     NOT NULL CHECK (kind IN ('full','event_odds','event_stateful')),
  event_urn          text,
  after_ts           timestamptz,
  node_id            integer,
  http_status        integer,
  requested_at       timestamptz NOT NULL DEFAULT now(),
  completed_at       timestamptz,                       -- snapshot_complete
  outcome            text CHECK (outcome IN ('completed','timeout','failed','superseded')),
  FOREIGN KEY (provider_id, producer_id) REFERENCES producer_status(provider_id, producer_id),
  UNIQUE (provider_id, producer_id, request_id)
);
