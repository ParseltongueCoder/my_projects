-- V006: BO-1 catalogue & translations: per-operator catalogue attributes, event overrides, media, translations.
-- Design: docs/06 §2-3 and §8; binding decisions docs/09 §2 (BO never edits sb rows; brand dimension for texts).
-- Inheritable policies (visibility, live/prematch) stay CFG settings (offer.*); these tables hold attributes.

SET search_path = bo;

-- Sort order, top flag and slug of a sport / category / tournament for one operator.
CREATE TABLE catalog_node (
  operator_id  bigint NOT NULL REFERENCES operator(id),
  node_type    text   NOT NULL CHECK (node_type IN ('sport', 'category', 'tournament')),
  node_id      bigint NOT NULL,                              -- sb.sport / sb.category / sb.tournament id
  sort_order   integer,                                      -- NULL: top first, then name
  is_top       boolean NOT NULL DEFAULT false,
  top_order    integer,
  slug         text CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
  updated_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1,
  PRIMARY KEY (operator_id, node_type, node_id)
);
CREATE UNIQUE INDEX catalog_node_slug_uq ON catalog_node (operator_id, node_type, slug) WHERE slug IS NOT NULL;

-- Display attributes of one feed event for one operator. Betting cut-off always follows sb.event (docs/06 §2.5.3).
CREATE TABLE event_override (
  operator_id       bigint NOT NULL REFERENCES operator(id),
  event_id          bigint NOT NULL,                         -- sb.event.id
  display_start_at  timestamptz,
  is_featured       boolean NOT NULL DEFAULT false,
  featured_order    integer,
  featured_from     timestamptz,
  featured_to       timestamptz,
  note              text,
  updated_by        uuid,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  version           integer NOT NULL DEFAULT 1,
  PRIMARY KEY (operator_id, event_id)
);
CREATE INDEX event_override_featured_idx ON event_override (operator_id) WHERE is_featured;

-- Uploaded images. Stored in the database until object storage + CDN exist (docs/06 §2.5.6); content-addressed.
CREATE TABLE media_asset (
  id           uuid PRIMARY KEY DEFAULT uuidv7(),
  operator_id  bigint REFERENCES operator(id),               -- NULL = platform set
  sha256       bytea  NOT NULL,
  mime         text   NOT NULL CHECK (mime IN ('image/png', 'image/jpeg', 'image/webp', 'image/svg+xml')),
  size_bytes   integer NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 524288),
  file_name    text,
  uploaded_by  uuid,
  created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX media_asset_sha_idx ON media_asset (operator_id, sha256);

CREATE TABLE media_blob (
  media_id  uuid PRIMARY KEY REFERENCES media_asset(id) ON DELETE CASCADE,
  content   bytea NOT NULL
);

-- Which image an entity shows; operator rows override platform rows (operator_id NULL).
CREATE TABLE media_link (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id  bigint REFERENCES operator(id),
  entity_type  text NOT NULL CHECK (entity_type IN ('sport', 'category', 'tournament', 'competitor', 'player', 'event')),
  entity_id    bigint NOT NULL,
  role         text NOT NULL CHECK (role IN ('icon', 'flag', 'logo', 'banner')),
  media_id     uuid NOT NULL REFERENCES media_asset(id),
  updated_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE NULLS NOT DISTINCT (operator_id, entity_type, entity_id, role)
);

-- Languages the platform knows (operators pick theirs: bo.operator.languages).
CREATE TABLE language (
  code          text PRIMARY KEY,
  name          text NOT NULL,
  native_name   text NOT NULL,
  ordinal_rule  text NOT NULL DEFAULT 'en'
);
INSERT INTO language (code, name, native_name, ordinal_rule) VALUES
  ('ka', 'Georgian', 'ქართული', 'ka'),
  ('en', 'English', 'English', 'en'),
  ('ru', 'Russian', 'Русский', 'ru'),
  ('tr', 'Turkish', 'Türkçe', 'tr'),
  ('uk', 'Ukrainian', 'Українська', 'ru'),
  ('az', 'Azerbaijani', 'Azərbaycan', 'tr'),
  ('hy', 'Armenian', 'Հայերեն', 'en');

-- Texts players see. Fallback: operator (brand, then operator-wide) → platform → provider (sb.*.name_i18n) → fallback langs.
-- entity_id is text: '123' for rows, '18' / '18::12' (market type / market type:variant:outcome) for templates.
CREATE TABLE translation (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  operator_id  bigint REFERENCES operator(id),               -- NULL = platform
  brand_id     bigint REFERENCES brand(id),                  -- NULL = every brand of the operator (docs/09 §2.16)
  entity_type  text NOT NULL CHECK (entity_type IN ('sport', 'category', 'tournament', 'event', 'competitor', 'player',
                                                    'market_type', 'outcome_type', 'message')),
  entity_id    text NOT NULL,
  field        text NOT NULL DEFAULT 'name' CHECK (field IN ('name', 'short_name', 'abbreviation', 'template', 'title', 'text')),
  lang         text NOT NULL REFERENCES language(code),
  text         text NOT NULL CHECK (length(text) BETWEEN 1 AND 500),
  status       text NOT NULL DEFAULT 'approved' CHECK (status IN ('approved', 'needs_review', 'machine')),
  source       text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'import', 'machine')),
  updated_by   uuid,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  version      integer NOT NULL DEFAULT 1,
  CHECK (brand_id IS NULL OR operator_id IS NOT NULL),
  UNIQUE NULLS NOT DISTINCT (operator_id, brand_id, entity_type, entity_id, field, lang)
);
CREATE INDEX translation_entity_idx ON translation (entity_type, entity_id);

-- =====================================================================
-- Row-level security (same pattern as V005)
-- =====================================================================
ALTER TABLE catalog_node ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON catalog_node USING (operator_id = bo.current_operator()) WITH CHECK (operator_id = bo.current_operator());
ALTER TABLE event_override ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant ON event_override USING (operator_id = bo.current_operator()) WITH CHECK (operator_id = bo.current_operator());

ALTER TABLE media_asset ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON media_asset FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON media_asset FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));
ALTER TABLE media_blob ENABLE ROW LEVEL SECURITY;
CREATE POLICY via_asset ON media_blob USING (EXISTS (SELECT 1 FROM bo.media_asset a WHERE a.id = media_id))
  WITH CHECK (EXISTS (SELECT 1 FROM bo.media_asset a WHERE a.id = media_id));

ALTER TABLE media_link ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON media_link FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON media_link FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

ALTER TABLE translation ENABLE ROW LEVEL SECURITY;
CREATE POLICY read ON translation FOR SELECT USING (operator_id IS NULL OR operator_id = bo.current_operator());
CREATE POLICY write ON translation FOR ALL
  USING (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()))
  WITH CHECK (operator_id = bo.current_operator() OR (operator_id IS NULL AND bo.is_platform()));

GRANT SELECT ON language TO bo_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON catalog_node, event_override, media_asset, media_blob, media_link, translation TO bo_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA bo TO bo_app;
