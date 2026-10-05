-- Dev catalogue for backoffice-odds-dev.mjs (no simulator needed): one soccer match, feed 1x2 2.10/3.40/3.60 and total 2.5 1.85/1.95.
-- Load after Bo.Api has migrated an empty database (Bo__DevSeed=true). Test data only, written by hand.
INSERT INTO sb.sport (code, name_i18n) VALUES ('soccer', '{"en":"Soccer","ka":"ფეხბურთი"}');
INSERT INTO sb.category (sport_id, name_i18n, country_code) VALUES (1, '{"en":"Georgia"}', 'GEO');
INSERT INTO sb.tournament (sport_id, category_id, name_i18n) VALUES (1, 1, '{"en":"Erovnuli Liga"}');
INSERT INTO sb.competitor (sport_id, name_i18n, country_code) VALUES (1, '{"en":"Dinamo Tbilisi"}', 'GEO'), (1, '{"en":"Torpedo Kutaisi"}', 'GEO');
INSERT INTO sb.market_description (code, name_template_i18n, groups) VALUES ('1x2', '{"en":"1x2"}', '{all,regular_play}'), ('total', '{"en":"Total"}', '{all,regular_play}');
INSERT INTO sb.market_specifier_def (market_description_id, name, type) VALUES (2, 'total', 'decimal');
INSERT INTO sb.market_description_outcome (market_description_id, code, name_template_i18n, ordinal) VALUES
  (1, '1', '{"en":"{$competitor1}"}', 1), (1, '2', '{"en":"draw"}', 2), (1, '3', '{"en":"{$competitor2}"}', 3),
  (2, '12', '{"en":"over {total}"}', 1), (2, '13', '{"en":"under {total}"}', 2);
INSERT INTO sb.event (event_type, sport_id, tournament_id, scheduled_at) VALUES ('match', 1, 1, now() + interval '1 day');
INSERT INTO sb.provider_mapping (provider_id, entity_type, provider_entity_id, internal_id) VALUES (1, 'event', 'sr:match:61000001', 1);
INSERT INTO sb.event_competitor (event_id, position, competitor_id, qualifier) VALUES (1, 1, 1, 'home'), (1, 2, 2, 'away');
INSERT INTO sb.market (event_id, market_description_id, specifiers, specifiers_json, status, feed_status, source_producer_id, last_feed_ts) VALUES
  (1, 1, '', '{}', 'active', 'active', 3, now()), (1, 2, 'total=2.5', '{"total":"2.5"}', 'active', 'active', 3, now());
INSERT INTO sb.outcome (market_id, code, description_outcome_id, odds, is_active, odds_updated_at) VALUES
  (1, '1', 1, 2.10, true, now()), (1, '2', 2, 3.40, true, now()), (1, '3', 3, 3.60, true, now()),
  (2, '12', 4, 1.85, true, now()), (2, '13', 5, 1.95, true, now());
