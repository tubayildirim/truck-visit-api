-- =============================================================================================
-- Capacity seed: one million visits.
--
-- The architecture document estimates ~51 million visits over the seven-year retention period and
-- argues that correct indexes, not sharding, are what that demands. That is arithmetic. This
-- script exists so the argument can be checked against a query planner instead of taken on trust.
--
-- One million rows is ~2% of the seven-year figure and already two orders of magnitude past the
-- point where a sequential scan would be obvious in the plan — enough to show whether the index
-- strategy works, without needing an hour and 80GB to find out.
--
-- Deliberately pessimistic in two ways:
--   * random UUIDs rather than the time-ordered v7 keys the application generates, so the index is
--     more fragmented here than in production;
--   * every terminal and status equally weighted, so no filter is unusually selective.
--
-- NOTE: the hashes below are synthetic. Seeded rows will NOT pass audit verification, and are not
-- meant to — they exist to give the planner realistic volume and column widths. Verify the audit
-- chain against data written through the API.
--
-- Usage:
--   docker compose exec -T postgres psql -U truckvisit -d truckvisit -f - < tools/capacity/seed.sql
-- =============================================================================================

\timing on
SET client_min_messages = warning;

-- ---------------------------------------------------------------------------------------------
-- Visits
-- ---------------------------------------------------------------------------------------------
INSERT INTO visits (
    "Id", "TerminalId", "CurrentStatus",
    truck_unit_number, truck_license_plate,
    driver_full_name, driver_document_id, driver_company_name, driver_phone_number,
    "CreatedTime", "CreatedBy", "LastStatusChangedAt")
SELECT
    gen_random_uuid(),
    (ARRAY['DOVER', 'HARWICH', 'CALAIS', 'IMMINGHAM', 'ROSTOCK'])[1 + (i % 5)],
    (ARRAY['PreRegistered', 'AtGate', 'OnSite', 'Completed'])[1 + (i % 4)],
    'MSCU' || lpad((i % 9999999)::text, 7, '0'),
    'PLATE' || lpad((i % 999999)::text, 6, '0'),
    'Seed Driver ' || i,
    'DOC' || i,
    (ARRAY['ACME HAULAGE', 'NORDIC TRANS', 'VIKING LOGISTICS', 'BALTIC CARGO'])[1 + (i % 4)],
    NULL,
    -- Spread across roughly two years at one visit every 63 seconds.
    TIMESTAMPTZ '2024-09-21 00:00:00+00' + (i * INTERVAL '63 seconds'),
    'seed-operator',
    TIMESTAMPTZ '2024-09-21 00:00:00+00' + (i * INTERVAL '63 seconds')
FROM generate_series(1, 1000000) AS i;

-- ---------------------------------------------------------------------------------------------
-- Movements — one per visit, completed only where the visit itself completed, which is what the
-- outstanding-work rule guarantees in real data.
-- ---------------------------------------------------------------------------------------------
INSERT INTO visit_movements ("Id", "Type", "UnitNumber", "From", "To", "VisitId", "CompletedAt", "CompletedBy")
SELECT
    gen_random_uuid(),
    CASE WHEN (random() < 0.5) THEN 'Delivery' ELSE 'Collection' END,
    v.truck_unit_number,
    (ARRAY['DEPOTA', 'DEPOTB', 'YARD1', 'YARD2'])[1 + (floor(random() * 4))::int],
    (ARRAY['BERTH1', 'BERTH2', 'BERTH3', 'YARD3'])[1 + (floor(random() * 4))::int],
    v."Id",
    CASE WHEN v."CurrentStatus" = 'Completed' THEN v."LastStatusChangedAt" END,
    CASE WHEN v."CurrentStatus" = 'Completed' THEN 'seed-crane' END
FROM visits v
WHERE v."CreatedBy" = 'seed-operator';

-- ---------------------------------------------------------------------------------------------
-- Audit trail — three entries per visit. Synthetic hashes; see the note at the top.
-- ---------------------------------------------------------------------------------------------
INSERT INTO visit_status_history (
    "Id", "VisitId", "Sequence", "From", "To", "ChangedAt", "ChangedBy", "Reason",
    "EntryHash", "PreviousHash")
SELECT
    gen_random_uuid(),
    v."Id",
    s.seq,
    CASE s.seq WHEN 1 THEN NULL WHEN 2 THEN 'PreRegistered' ELSE 'AtGate' END,
    CASE s.seq WHEN 1 THEN 'PreRegistered' WHEN 2 THEN 'AtGate' ELSE 'OnSite' END,
    v."CreatedTime" + (s.seq * INTERVAL '10 minutes'),
    'seed-operator',
    NULL,
    encode(sha256((v."Id"::text || ':' || s.seq::text)::bytea), 'hex'),
    CASE WHEN s.seq > 1
         THEN encode(sha256((v."Id"::text || ':' || (s.seq - 1)::text)::bytea), 'hex')
    END
FROM visits v
CROSS JOIN generate_series(1, 3) AS s(seq)
WHERE v."CreatedBy" = 'seed-operator';

-- Without fresh statistics the planner is guessing, and a plan chosen from stale estimates tells
-- you nothing about the index strategy.
ANALYZE visits;
ANALYZE visit_movements;
ANALYZE visit_status_history;

SELECT
    (SELECT count(*) FROM visits)               AS visits,
    (SELECT count(*) FROM visit_movements)      AS movements,
    (SELECT count(*) FROM visit_status_history) AS audit_entries,
    pg_size_pretty(
        pg_total_relation_size('visits')
        + pg_total_relation_size('visit_movements')
        + pg_total_relation_size('visit_status_history')) AS total_size;
