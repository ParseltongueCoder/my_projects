-- V004: push changes to the Feed Ops admin (admin-api LISTENs on channel 'sb_changes' and forwards
-- them to browsers over SSE). Payloads are tiny ({table, id, eventId}); NOTIFY collapses identical
-- payloads within one transaction, so a message touching many markets of one event stays cheap.
SET search_path = sb;

CREATE OR REPLACE FUNCTION notify_sb_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  -- The tables have different columns; go through jsonb so one function serves all of them.
  r jsonb := to_jsonb(NEW);
BEGIN
  PERFORM pg_notify('sb_changes', json_build_object(
    'table', TG_TABLE_NAME,
    'eventId', CASE TG_TABLE_NAME WHEN 'event' THEN r->'id' WHEN 'market' THEN r->'event_id' END,
    'producerId', CASE TG_TABLE_NAME WHEN 'producer_status' THEN r->'producer_id' END
  )::text);
  RETURN NEW;
END $$;

CREATE TRIGGER event_notify AFTER INSERT OR UPDATE ON event
  FOR EACH ROW EXECUTE FUNCTION notify_sb_change();
CREATE TRIGGER market_notify AFTER INSERT OR UPDATE ON market
  FOR EACH ROW EXECUTE FUNCTION notify_sb_change();
CREATE TRIGGER producer_status_notify AFTER INSERT OR UPDATE ON producer_status
  FOR EACH ROW EXECUTE FUNCTION notify_sb_change();
