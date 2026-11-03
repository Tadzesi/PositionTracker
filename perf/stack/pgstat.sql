-- One row per second (psql \watch): database activity counters, WAL and checkpointer; the analysis differentiates them.
SELECT round(extract(epoch FROM clock_timestamp()) * 1000)::bigint,
       d.xact_commit, d.xact_rollback, d.tup_inserted, d.tup_returned, d.tup_fetched, d.blks_read, d.blks_hit,
       (SELECT count(*) FROM pg_stat_activity WHERE datname = 'positiontracker' AND state = 'active'),
       (SELECT count(*) FROM pg_stat_activity WHERE datname = 'positiontracker'),
       (SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'),
       w.wal_records, w.wal_bytes, w.wal_write, w.wal_sync,
       c.num_timed, c.num_requested, c.buffers_written, c.write_time, c.sync_time,
       coalesce((SELECT n_tup_ins FROM pg_stat_user_tables WHERE relname = 'Fixes'), 0),
       pg_database_size('positiontracker')
FROM pg_stat_database d, pg_stat_wal w, pg_stat_checkpointer c
WHERE d.datname = 'positiontracker'
\watch 1
