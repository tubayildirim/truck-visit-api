-- =============================================================================================
-- Capacity check: does the index strategy actually hold at volume?
--
-- Run after seed.sql. Each query below mirrors one the application issues, so a bad plan here is
-- a bad plan in production — not an approximation of one.
--
-- What to look for:
--   * "Index Scan" or "Index Only Scan" on the ix_visits_* indexes, never "Seq Scan" on visits
--   * execution time in single-digit milliseconds
--   * "rows removed by filter" close to zero — a high number means the index is being used to
--     reach rows that are then thrown away, which is a scan wearing a disguise
--
-- Usage:
--   docker compose exec -T postgres psql -U truckvisit -d truckvisit -f - < tools/capacity/explain.sql
-- =============================================================================================

\timing on

\echo
\echo '=== 1. The dominant query: one terminal, one status, most recent first ==================='
\echo '    Should seek on ix_visits_terminal_status_created.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT v."Id", v."TerminalId", v."CurrentStatus", v.truck_unit_number, v.truck_license_plate,
       v.driver_full_name, v.driver_company_name, v."CreatedTime", v."CreatedBy",
       v."LastStatusChangedAt"
FROM visits v
WHERE v."TerminalId" = 'DOVER'
  AND v."CurrentStatus" = 'OnSite'
ORDER BY v."CreatedTime" DESC, v."Id" DESC
LIMIT 25;

\echo
\echo '=== 2. The paging count that accompanies it ============================================='
\echo '    The expensive half of offset pagination, and why keyset is the documented next step.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT count(*)
FROM visits v
WHERE v."TerminalId" = 'DOVER'
  AND v."CurrentStatus" = 'OnSite';

\echo
\echo '=== 3. Everything at one terminal in a date window ======================================'
\echo '    Should seek on ix_visits_terminal_created.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT v."Id", v."CreatedTime"
FROM visits v
WHERE v."TerminalId" = 'HARWICH'
  AND v."CreatedTime" >= TIMESTAMPTZ '2025-06-01 00:00:00+00'
  AND v."CreatedTime" <  TIMESTAMPTZ '2025-07-01 00:00:00+00'
ORDER BY v."CreatedTime" DESC, v."Id" DESC
LIMIT 25;

\echo
\echo '=== 4. Filtering by movement origin (the movementFrom parameter) ========================'
\echo '    A semi-join through visit_movements; should use IX_visit_movements_From.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT v."Id"
FROM visits v
WHERE v."TerminalId" = 'CALAIS'
  AND EXISTS (
      SELECT 1 FROM visit_movements m
      WHERE m."VisitId" = v."Id" AND m."From" = 'YARD1')
ORDER BY v."CreatedTime" DESC, v."Id" DESC
LIMIT 25;

\echo
\echo '=== 5. The gate worklist: on site with work outstanding ================================='
\echo '    The query a gate screen polls; uses the partial-null side of IX_visit_movements_CompletedAt.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT v."Id"
FROM visits v
WHERE v."TerminalId" = 'ROSTOCK'
  AND v."CurrentStatus" = 'OnSite'
  AND EXISTS (
      SELECT 1 FROM visit_movements m
      WHERE m."VisitId" = v."Id" AND m."CompletedAt" IS NULL)
ORDER BY v."CreatedTime" DESC, v."Id" DESC
LIMIT 25;

\echo
\echo '=== 6. Loading one visit with its full audit trail ======================================'
\echo '    The detail endpoint. Primary key plus the FK index on the history table.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT h."Sequence", h."From", h."To", h."ChangedAt", h."ChangedBy", h."EntryHash"
FROM visit_status_history h
WHERE h."VisitId" = (SELECT "Id" FROM visits WHERE "TerminalId" = 'DOVER' LIMIT 1)
ORDER BY h."Sequence";

\echo
\echo '=== 7. Deep pagination — the documented limitation ======================================'
\echo '    Page 2000 of 25. Compare the time with query 1: this is the cost offset paging carries,'
\echo '    and the reason keyset pagination on (CreatedTime, Id) is the next step rather than a'
\echo '    speculative one.'
EXPLAIN (ANALYZE, BUFFERS)
SELECT v."Id"
FROM visits v
WHERE v."TerminalId" = 'DOVER'
ORDER BY v."CreatedTime" DESC, v."Id" DESC
LIMIT 25 OFFSET 50000;

\echo
\echo '=== Index sizes ========================================================================'
SELECT indexrelname AS index, pg_size_pretty(pg_relation_size(indexrelid)) AS size, idx_scan AS scans
FROM pg_stat_user_indexes
WHERE relname IN ('visits', 'visit_movements', 'visit_status_history')
ORDER BY pg_relation_size(indexrelid) DESC;
