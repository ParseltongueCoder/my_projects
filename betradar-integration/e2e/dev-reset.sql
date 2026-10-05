-- Undo what backoffice-odds-dev.mjs changes, so the script can run again on the same database.
DELETE FROM bo.odds_override; DELETE FROM bo.trading_override;
DELETE FROM sb.outcome WHERE market_id IN (SELECT id FROM sb.market WHERE source_producer_id = 0);
DELETE FROM bo.manual_entity; DELETE FROM sb.market WHERE source_producer_id = 0;
DELETE FROM bo.translation WHERE entity_type = 'message';
DELETE FROM bo.setting WHERE key = 'market.enabled';
