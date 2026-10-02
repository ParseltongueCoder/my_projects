-- V002: what the first running adapter needed on top of the documented model.
SET search_path = sb;

-- feed_message_log is partitioned by received_at; docs/02 assumes pg_partman creates daily
-- partitions. Until that is set up, a default partition keeps inserts working.
CREATE TABLE IF NOT EXISTS feed_message_log_default PARTITION OF feed_message_log DEFAULT;

-- The documented unique index (payload_sha256, received_at) cannot deduplicate (received_at differs
-- per delivery, see the note in V001). Dedup is done by the adapter; drop the misleading index.
DROP INDEX IF EXISTS feed_log_dedup_uq;
CREATE INDEX IF NOT EXISTS feed_log_sha_idx ON feed_message_log (payload_sha256);

-- A certainty upgrade (1 → 2) inserts the new settlement and points the old one at it in one
-- transaction. With the partial unique index "one effective settlement per outcome", the old row has
-- to stop being effective before the new row exists, so the self-reference must be checked at commit.
ALTER TABLE settlement ALTER CONSTRAINT settlement_superseded_by_id_fkey DEFERRABLE INITIALLY DEFERRED;
