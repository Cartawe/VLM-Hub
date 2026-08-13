using Microsoft.Data.Sqlite;

namespace VlmHub.Sql.Persistence;

/// <summary>
/// Historial local. No decide qué elemento debe procesarse; esa autoridad sigue
/// en control/*.json. Cada operación abre una conexión corta y escribe solo un
/// documento/intento, por lo que el historial nunca se acumula en memoria.
/// </summary>
public sealed class LocalHistoryStore
{
    private readonly string _connectionString;

    public LocalHistoryStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS "Document"
            (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProcessingId" TEXT NOT NULL,
                "ServerHost" TEXT NOT NULL,
                "DatabaseName" TEXT NOT NULL,
                "SourceTable" TEXT NOT NULL,
                "OriginalKey" TEXT NOT NULL,
                "DocumentName" TEXT NULL,
                "ServerProcessingId" INTEGER NULL,
                "Success" INTEGER NULL,
                "ProcessingTime" REAL NULL,
                "StartedAt" TEXT NOT NULL,
                "FinishedAt" TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_Document_ProcessingId"
                ON "Document" ("ProcessingId");

            CREATE TABLE IF NOT EXISTS "ProcessingUnit"
            (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DocumentId" TEXT NOT NULL,
                "UnitIndex" INTEGER NOT NULL,
                "AttemptNumber" INTEGER NOT NULL,
                "Model" TEXT NULL,
                "Server" TEXT NULL,
                "Success" INTEGER NOT NULL,
                "FinishReason" TEXT NULL,
                "ProcessingTime" REAL NULL,
                "ProcessedAt" TEXT NOT NULL,
                "Transcription" TEXT NULL,
                "Error" TEXT NULL,
                FOREIGN KEY ("DocumentId") REFERENCES "Document" ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProcessingUnit_Attempt"
                ON "ProcessingUnit" ("DocumentId", "UnitIndex", "AttemptNumber");

            CREATE INDEX IF NOT EXISTS "IX_ProcessingUnit_DocumentId"
                ON "ProcessingUnit" ("DocumentId");
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        // Migraciones pequeñas e idempotentes para instalaciones existentes.
        // Se ejecutan una sola vez al abrir el historial, no por documento.
        await EnsureColumnAsync(
            connection,
            "Document",
            "DocumentName",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "Document",
            "ProcessingTime",
            "REAL NULL",
            cancellationToken);
    }

    public async Task BeginDocumentAsync(
        LocalDocumentRecord document,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Document"
            (
                "Id", "ProcessingId", "ServerHost", "DatabaseName", "SourceTable",
                "OriginalKey", "DocumentName", "StartedAt"
            )
            VALUES
            (
                $id, $processingId, $serverHost, $databaseName, $sourceTable,
                $originalKey, $documentName, $startedAt
            )
            ON CONFLICT("Id") DO UPDATE SET
                "ProcessingId" = excluded."ProcessingId",
                "ServerHost" = excluded."ServerHost",
                "DatabaseName" = excluded."DatabaseName",
                "SourceTable" = excluded."SourceTable",
                "OriginalKey" = excluded."OriginalKey",
                "DocumentName" = COALESCE(excluded."DocumentName", "Document"."DocumentName");
            """;

        command.Parameters.AddWithValue("$id", document.Id.ToString("N"));
        command.Parameters.AddWithValue("$processingId", document.ProcessingId.ToString("N"));
        command.Parameters.AddWithValue("$serverHost", document.ServerHost);
        command.Parameters.AddWithValue("$databaseName", document.DatabaseName);
        command.Parameters.AddWithValue("$sourceTable", document.SourceTable);
        command.Parameters.AddWithValue("$originalKey", document.OriginalKey);
        command.Parameters.AddWithValue("$documentName", DbValue(document.DocumentName));
        command.Parameters.AddWithValue("$startedAt", document.StartedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAttemptAsync(
        ProcessingUnitAttemptRecord attempt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ProcessingUnit"
            (
                "DocumentId", "UnitIndex", "AttemptNumber", "Model", "Server",
                "Success", "FinishReason", "ProcessingTime", "ProcessedAt",
                "Transcription", "Error"
            )
            SELECT
                $documentId,
                $unitIndex,
                COALESCE(
                    (SELECT MAX("AttemptNumber") + 1
                     FROM "ProcessingUnit"
                     WHERE "DocumentId" = $documentId
                       AND "UnitIndex" = $unitIndex),
                    1),
                $model, $server, $success, $finishReason, $processingTime,
                $processedAt, $transcription, $error;
            """;

        command.Parameters.AddWithValue("$documentId", attempt.DocumentId.ToString("N"));
        command.Parameters.AddWithValue("$unitIndex", attempt.UnitIndex);
        command.Parameters.AddWithValue("$model", DbValue(attempt.Model));
        command.Parameters.AddWithValue("$server", DbValue(attempt.Server));
        command.Parameters.AddWithValue("$success", attempt.Success ? 1 : 0);
        command.Parameters.AddWithValue("$finishReason", DbValue(attempt.FinishReason));
        command.Parameters.AddWithValue("$processingTime", DbValue(attempt.ProcessingTime));
        command.Parameters.AddWithValue("$processedAt", attempt.ProcessedAt.ToString("O"));
        command.Parameters.AddWithValue("$transcription", DbValue(attempt.Transcription));
        command.Parameters.AddWithValue("$error", DbValue(attempt.Error));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteDocumentAsync(
        Guid documentId,
        bool success,
        DateTimeOffset finishedAt,
        double? processingTime,
        long? serverProcessingId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Document"
            SET "Success" = $success,
                "FinishedAt" = $finishedAt,
                "ProcessingTime" = $processingTime,
                "ServerProcessingId" = COALESCE($serverProcessingId, "ServerProcessingId")
            WHERE "Id" = $id;
            """;

        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("O"));
        command.Parameters.AddWithValue("$processingTime", DbValue(processingTime));
        command.Parameters.AddWithValue("$serverProcessingId", DbValue(serverProcessingId));
        command.Parameters.AddWithValue("$id", documentId.ToString("N"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetServerProcessingIdAsync(
        Guid documentId,
        long serverProcessingId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Document"
            SET "ServerProcessingId" = $serverProcessingId
            WHERE "Id" = $id;
            """;
        command.Parameters.AddWithValue("$serverProcessingId", serverProcessingId);
        command.Parameters.AddWithValue("$id", documentId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
        try
        {
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 1 &&
            exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Otro worker pudo completar la misma migración entre PRAGMA y ALTER.
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
