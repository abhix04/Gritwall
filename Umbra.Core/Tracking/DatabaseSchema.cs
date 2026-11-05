namespace Umbra.Core.Tracking;

internal static class DatabaseSchema
{
    public const int CurrentVersion = 1;

    public const string CreateSchemaV1 = """
        CREATE TABLE IF NOT EXISTS AppActivityIntervals (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BootId TEXT NOT NULL,
            ProcessName TEXT NOT NULL,
            ExecutablePath TEXT NOT NULL,
            State TEXT NOT NULL,
            StartTimeUtc TEXT NOT NULL,
            EndTimeUtc TEXT NULL,
            LastHeartbeatUtc TEXT NOT NULL,
            StartUnbiasedTime INTEGER NOT NULL,
            EndUnbiasedTime INTEGER NULL,
            LastHeartbeatUnbiasedTime INTEGER NOT NULL,
            DurationSeconds REAL NULL,
            Status TEXT NOT NULL
        );

        -- Enforce at most ONE open interval at any time across the timeline
        CREATE UNIQUE INDEX IF NOT EXISTS UQ_AppActivityIntervals_SingleOpen
            ON AppActivityIntervals(Status)
            WHERE Status = 'Open';

        CREATE INDEX IF NOT EXISTS IX_AppActivityIntervals_Boot_Status
            ON AppActivityIntervals(BootId, Status);

        CREATE INDEX IF NOT EXISTS IX_AppActivityIntervals_StartTimeUtc
            ON AppActivityIntervals(StartTimeUtc);

        CREATE INDEX IF NOT EXISTS IX_AppActivityIntervals_ProcessName
            ON AppActivityIntervals(ProcessName);
        """;
}
