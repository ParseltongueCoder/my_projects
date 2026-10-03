-- V003: separate what the feed says about a market from what we offer.
--   feed_status: the status the provider last sent (odds_change / bet_stop / settlement ...)
--   status:      the effective status - equal to feed_status, except that markets of a producer that is
--                down stay suspended until the producer is back up (after its recovery snapshot).
-- Without this, odds arriving while a producer is down would re-open its markets too early, and after
-- the recovery we would not know which suspended markets the feed itself had suspended.
SET search_path = sb;

ALTER TABLE market ADD COLUMN feed_status market_status;
UPDATE market SET feed_status = status;
ALTER TABLE market ALTER COLUMN feed_status SET NOT NULL;
