CREATE TABLE IF NOT EXISTS captures (
    id uuid PRIMARY KEY,
    device_serial text NOT NULL,
    status text NOT NULL,
    captured_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    payload jsonb NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_captures_device_time
    ON captures (device_serial, captured_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_captures_status_time
    ON captures (status, captured_at_utc DESC);
