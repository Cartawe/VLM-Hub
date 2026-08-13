using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using VlmHub.Sql.Models;

namespace VlmHub.Sql;

/// <summary>
/// Encapsula la conexión y las consultas necesarias para navegar y extraer
/// documentos desde Microsoft SQL Server.
/// </summary>
public sealed class SqlServer
{
    private readonly SqlConnectionStringBuilder _builder;

    public string ServerHost => _builder.DataSource;
    public string User => _builder.UserID;
    public string? Database { get; private set; }

    public SqlServer(string serverHost, string user, string password)
    {
        _builder = new SqlConnectionStringBuilder
        {
            DataSource = serverHost,
            UserID = user,
            Password = password,
            ConnectTimeout = 10,
            CommandTimeout = 30,
            TrustServerCertificate = true
        };
    }

    public void SetDatabase(string database)
    {
        Database = database;
        _builder.InitialCatalog = database;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state_desc = 'ONLINE'
              AND HAS_DBACCESS(name) = 1
            ORDER BY name;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var databases = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }

    public async Task<IReadOnlyList<string>> GetTablesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT
                s.name AS TABLE_SCHEMA,
                t.name AS TABLE_NAME
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tables = new List<string>();
        var schemaOrdinal = reader.GetOrdinal("TABLE_SCHEMA");
        var tableOrdinal = reader.GetOrdinal("TABLE_NAME");

        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add($"{reader.GetString(schemaOrdinal)}.{reader.GetString(tableOrdinal)}");
        }

        return tables;
    }

    /// <summary>
    /// Obtiene las columnas de una tabla y la posición de las que forman su PK.
    /// </summary>
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(tableName);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT
                c.name AS COLUMN_NAME,
                ty.name AS DATA_TYPE,
                CASE
                    WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary') THEN
                        ty.name + '(' +
                        CASE
                            WHEN c.max_length = -1 THEN 'max'
                            ELSE CONVERT(varchar(10), c.max_length)
                        END + ')'

                    WHEN ty.name IN ('nvarchar', 'nchar') THEN
                        ty.name + '(' +
                        CASE
                            WHEN c.max_length = -1 THEN 'max'
                            ELSE CONVERT(varchar(10), c.max_length / 2)
                        END + ')'

                    WHEN ty.name IN ('decimal', 'numeric') THEN
                        ty.name + '(' +
                        CONVERT(varchar(10), c.precision) + ',' +
                        CONVERT(varchar(10), c.scale) + ')'

                    WHEN ty.name IN ('datetime2', 'datetimeoffset', 'time') THEN
                        ty.name + '(' + CONVERT(varchar(10), c.scale) + ')'

                    ELSE ty.name
                END AS COLUMN_TYPE,
                c.column_id AS ORDINAL_POSITION,
                c.is_nullable AS IS_NULLABLE,
                c.collation_name AS COLLATION_NAME,
                ic.key_ordinal AS PK_POSITION
            FROM sys.tables t
            INNER JOIN sys.schemas s
                ON s.schema_id = t.schema_id
            INNER JOIN sys.columns c
                ON c.object_id = t.object_id
            INNER JOIN sys.types ty
                ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.indexes i
                ON i.object_id = t.object_id
               AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic
                ON ic.object_id = i.object_id
               AND ic.index_id = i.index_id
               AND ic.column_id = c.column_id
            WHERE s.name = @schema
              AND t.name = @table
            ORDER BY c.column_id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<ColumnInfo>();

        var nameOrdinal = reader.GetOrdinal("COLUMN_NAME");
        var dataTypeOrdinal = reader.GetOrdinal("DATA_TYPE");
        var columnTypeOrdinal = reader.GetOrdinal("COLUMN_TYPE");
        var positionOrdinal = reader.GetOrdinal("ORDINAL_POSITION");
        var nullableOrdinal = reader.GetOrdinal("IS_NULLABLE");
        var collationOrdinal = reader.GetOrdinal("COLLATION_NAME");
        var pkPositionOrdinal = reader.GetOrdinal("PK_POSITION");

        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(nameOrdinal),
                DataType = reader.GetString(dataTypeOrdinal),
                ColumnType = reader.GetString(columnTypeOrdinal),
                Position = reader.GetInt32(positionOrdinal),
                IsNullable = reader.GetBoolean(nullableOrdinal),
                Collation = reader.IsDBNull(collationOrdinal) ? null : reader.GetString(collationOrdinal),
                PrimaryKeyPosition = reader.IsDBNull(pkPositionOrdinal)
                    ? null
                    : Convert.ToInt32(reader.GetValue(pkPositionOrdinal), CultureInfo.InvariantCulture)
            });
        }

        return columns;
    }

    /// <summary>
    /// Obtiene la PK de la primera y última fila usando el índice de la clave primaria.
    /// La consulta trae únicamente las columnas PK.
    /// </summary>
    public async Task<PrimaryKeyBounds> GetPrimaryKeyBoundsAsync(
        string tableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(tableName);
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var selectedColumns = string.Join(", ", primaryKeyColumns.Select(column => QuoteIdentifier(column.Name)));
        var ascendingOrder = string.Join(", ", primaryKeyColumns.Select(column => $"{QuoteIdentifier(column.Name)} ASC"));
        var descendingOrder = string.Join(", ", primaryKeyColumns.Select(column => $"{QuoteIdentifier(column.Name)} DESC"));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT TOP (1) {selectedColumns}
            FROM {qualifiedTable}
            ORDER BY {ascendingOrder};

            SELECT TOP (1) {selectedColumns}
            FROM {qualifiedTable}
            ORDER BY {descendingOrder};
            """;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        PrimaryKeyValue? first = null;
        PrimaryKeyValue? last = null;

        if (await reader.ReadAsync(cancellationToken))
        {
            first = ReadPrimaryKey(reader, primaryKeyColumns.Count);
        }

        if (await reader.NextResultAsync(cancellationToken) &&
            await reader.ReadAsync(cancellationToken))
        {
            last = ReadPrimaryKey(reader, primaryKeyColumns.Count);
        }

        return new PrimaryKeyBounds(first, last);
    }

    /// <summary>
    /// Ejecuta la selección real y escribe el resultado directamente en un JSON temporal.
    /// No existe una validación previa de tipos o existencia: SQL Server decide si la
    /// selección es válida. Solo se consultan las columnas PK y la columna documental.
    /// </summary>
    public async Task<SelectionExportResult?> ExportSelectionManifestAsync(
        string tableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        ColumnInfo documentColumn,
        IReadOnlyList<PrimaryKeySelectionPart> selection,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(tableName);
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var primaryKeyNames = primaryKeyColumns
            .Select(column => QuoteIdentifier(column.Name))
            .ToArray();
        var selectedColumns = string.Join(", ", primaryKeyNames.Append(QuoteIdentifier(documentColumn.Name)));
        var orderBy = string.Join(", ", primaryKeyNames);
        var tempPath = $"{destinationPath}.tmp";

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TryDeleteFile(tempPath);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            var predicates = new List<string>(selection.Count);
            for (var index = 0; index < selection.Count; index++)
            {
                predicates.Add(AddSelectionPredicate(
                    command,
                    primaryKeyColumns,
                    selection[index],
                    index));
            }

            // Una sola consulta: no se duplican los mismos predicados mediante
            // EXISTS previos. SQL Server resuelve directamente la selección y
            // un resultado vacío se maneja con el primer ReadAsync.
            command.CommandText = $"""
                SELECT {selectedColumns}
                FROM {qualifiedTable}
                WHERE {string.Join(" OR ", predicates)}
                ORDER BY {orderBy};
                """;

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            long rowCount = 0;

            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions
                {
                    Indented = false
                });

                writer.WriteStartArray();

                do
                {
                    writer.WriteStartObject();
                    writer.WriteString("item_id", Guid.NewGuid());
                    writer.WritePropertyName("primary_key");
                    writer.WriteStartObject();

                    for (var index = 0; index < primaryKeyColumns.Count; index++)
                    {
                        WriteSqlValue(
                            writer,
                            primaryKeyColumns[index].Name,
                            reader.GetValue(index));
                    }

                    writer.WriteEndObject();

                    var documentOrdinal = primaryKeyColumns.Count;
                    writer.WritePropertyName("document_url");
                    if (reader.IsDBNull(documentOrdinal))
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        var document = Convert.ToString(
                            reader.GetValue(documentOrdinal),
                            CultureInfo.InvariantCulture);
                        writer.WriteStringValue(NormalizeDocumentUrl(document));
                    }

                    writer.WriteEndObject();
                    rowCount++;

                    if ((rowCount & 255) == 0)
                    {
                        await writer.FlushAsync(cancellationToken);
                    }
                }
                while (await reader.ReadAsync(cancellationToken));

                writer.WriteEndArray();
                await writer.FlushAsync(cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return new SelectionExportResult(destinationPath, rowCount);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Garantiza la tabla permanente de resultados para la tabla de origen.
    /// SQL Server es quien acepta o rechaza el DDL; no se ejecuta una segunda
    /// capa de validaciones locales sobre tipos o permisos.
    /// </summary>
    public async Task<string> EnsureAuxiliaryTableAsync(
        string tableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(tableName);
        var auxiliaryTable = $"{table}_VlmHubProcessing";
        var qualifiedAuxiliary = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(auxiliaryTable)}";
        var objectNameLiteral = EscapeSqlLiteral($"{schema}.{auxiliaryTable}");
        var legacyProcessingTimeNameLiteral = EscapeSqlLiteral(
            $"{QuoteIdentifier(schema)}.{QuoteIdentifier(auxiliaryTable)}.[TotalProcessingTime]");

        var originalColumns = primaryKeyColumns.Select(column =>
        {
            var collation = string.IsNullOrWhiteSpace(column.Collation)
                ? string.Empty
                : $" COLLATE {QuoteIdentifier(column.Collation)}";
            return $"{QuoteIdentifier($"Original_{column.Name}")} {column.ColumnType}{collation} NOT NULL";
        });

        var primaryKeyIndexColumns = string.Join(", ",
            primaryKeyColumns
                .Select(column => QuoteIdentifier($"Original_{column.Name}"))
                .Append(QuoteIdentifier("Success")));

        var pkConstraint = SafeDbObjectName($"PK_{auxiliaryTable}");
        var processingKeyIndex = SafeDbObjectName($"UX_{auxiliaryTable}_ProcessingKey");
        var originalSuccessIndex = SafeDbObjectName($"IX_{auxiliaryTable}_OriginalPK_Success");

        var columnSql = string.Join(",\n                ", originalColumns);

        var sql = $"""
            IF OBJECT_ID(N'{objectNameLiteral}', N'U') IS NULL
            BEGIN
                CREATE TABLE {qualifiedAuxiliary}
                (
                    [Id] bigint IDENTITY(1,1) NOT NULL,
                    {columnSql},
                    [ProcessingKey] uniqueidentifier NOT NULL,
                    [Transcription] nvarchar(max) NULL,
                    [Success] bit NOT NULL,
                    [NumPages] int NULL,
                    [ProcessedAt] datetimeoffset(7) NOT NULL,
                    [ProcessingTime] decimal(18,3) NULL,
                    [Error] nvarchar(max) NULL,
                    CONSTRAINT {QuoteIdentifier(pkConstraint)} PRIMARY KEY CLUSTERED ([Id])
                );
            END;

            IF COL_LENGTH(N'{objectNameLiteral}', N'NumPages') IS NULL
            BEGIN
                ALTER TABLE {qualifiedAuxiliary}
                ADD [NumPages] int NULL;
            END;

            -- Conserva datos de instalaciones anteriores al cambio de nombre.
            IF COL_LENGTH(N'{objectNameLiteral}', N'ProcessingTime') IS NULL
               AND COL_LENGTH(N'{objectNameLiteral}', N'TotalProcessingTime') IS NOT NULL
            BEGIN
                EXEC sys.sp_rename
                    N'{legacyProcessingTimeNameLiteral}',
                    N'ProcessingTime',
                    N'COLUMN';
            END;

            IF COL_LENGTH(N'{objectNameLiteral}', N'ProcessingTime') IS NULL
            BEGIN
                ALTER TABLE {qualifiedAuxiliary}
                ADD [ProcessingTime] decimal(18,3) NULL;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{objectNameLiteral}')
                  AND name = N'{EscapeSqlLiteral(processingKeyIndex)}'
            )
            BEGIN
                CREATE UNIQUE INDEX {QuoteIdentifier(processingKeyIndex)}
                ON {qualifiedAuxiliary} ([ProcessingKey]);
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{objectNameLiteral}')
                  AND name = N'{EscapeSqlLiteral(originalSuccessIndex)}'
            )
            BEGIN
                CREATE INDEX {QuoteIdentifier(originalSuccessIndex)}
                ON {qualifiedAuxiliary} ({primaryKeyIndexColumns});
            END;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return $"{schema}.{auxiliaryTable}";
    }

    /// <summary>
    /// Recorre progresivamente las PK de filas que nunca han tenido Success=1.
    /// Queda disponible para futuras opciones de selección sin materializar toda
    /// la tabla original en memoria.
    /// </summary>
    public async IAsyncEnumerable<PrimaryKeyValue> GetRowsWithoutSuccessfulProcessingAsync(
        string tableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(tableName);
        var auxiliaryTable = $"{table}_VlmHubProcessing";
        var originalQualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var auxiliaryQualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(auxiliaryTable)}";
        var selectedColumns = string.Join(", ",
            primaryKeyColumns.Select(column => $"o.{QuoteIdentifier(column.Name)}"));
        var comparisons = string.Join(" AND ",
            primaryKeyColumns.Select(column =>
                $"p.{QuoteIdentifier($"Original_{column.Name}")} = o.{QuoteIdentifier(column.Name)}"));
        var orderBy = string.Join(", ",
            primaryKeyColumns.Select(column => $"o.{QuoteIdentifier(column.Name)}"));

        var sql = $"""
            SELECT {selectedColumns}
            FROM {originalQualified} AS o
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM {auxiliaryQualified} AS p
                WHERE {comparisons}
                  AND p.[Success] = 1
            )
            ORDER BY {orderBy};
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return ReadPrimaryKey(reader, primaryKeyColumns.Count);
        }
    }

    /// <summary>
    /// Inserta el resultado documental una sola vez. ProcessingKey serializa el
    /// reintento: si la confirmación SQL se pierde, repetir devuelve el Id ya
    /// existente en vez de duplicar la ejecución.
    /// </summary>
    public async Task<long> UpsertProcessingResultAsync(
        string auxiliaryTableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        IReadOnlyDictionary<string, JsonElement> primaryKey,
        Guid processingKey,
        string? resultFilePath,
        bool success,
        int? numPages,
        DateTimeOffset processedAt,
        double? processingTime,
        string? error,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        var (schema, table) = SplitTableName(auxiliaryTableName);
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var originalNames = primaryKeyColumns
            .Select(column => QuoteIdentifier($"Original_{column.Name}"))
            .ToArray();
        var originalParameters = primaryKeyColumns
            .Select((_, index) => $"@pk{index}")
            .ToArray();

        var transcription = resultFilePath is not null && File.Exists(resultFilePath)
            ? await File.ReadAllTextAsync(resultFilePath, cancellationToken)
            : null;

        var sql = $"""
            SET XACT_ABORT ON;
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;

            DECLARE @ExistingId bigint;

            SELECT @ExistingId = [Id]
            FROM {qualifiedTable} WITH (UPDLOCK, HOLDLOCK)
            WHERE [ProcessingKey] = @processingKey;

            IF @ExistingId IS NULL
            BEGIN
                INSERT INTO {qualifiedTable}
                (
                    {string.Join(", ", originalNames)},
                    [ProcessingKey], [Transcription], [Success], [NumPages], [ProcessedAt],
                    [ProcessingTime], [Error]
                )
                VALUES
                (
                    {string.Join(", ", originalParameters)},
                    @processingKey, @transcription, @success, @numPages, @processedAt,
                    @processingTime, @error
                );

                SET @ExistingId = CONVERT(bigint, SCOPE_IDENTITY());
            END;

            COMMIT TRANSACTION;
            SELECT @ExistingId;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        for (var index = 0; index < primaryKeyColumns.Count; index++)
        {
            var column = primaryKeyColumns[index];
            var value = primaryKey[column.Name];
            command.Parameters.AddWithValue(
                originalParameters[index],
                JsonPrimaryKeyValue(column, value));
        }

        command.Parameters.Add("@processingKey", SqlDbType.UniqueIdentifier).Value = processingKey;
        command.Parameters.Add("@transcription", SqlDbType.NVarChar, -1).Value =
            (object?)transcription ?? DBNull.Value;
        command.Parameters.Add("@success", SqlDbType.Bit).Value = success;
        command.Parameters.Add("@numPages", SqlDbType.Int).Value = (object?)numPages ?? DBNull.Value;
        command.Parameters.Add("@processedAt", SqlDbType.DateTimeOffset).Value = processedAt;

        var timeParameter = command.Parameters.Add("@processingTime", SqlDbType.Decimal);
        timeParameter.Precision = 18;
        timeParameter.Scale = 3;
        timeParameter.Value = processingTime is null
            ? DBNull.Value
            : Convert.ToDecimal(processingTime.Value, CultureInfo.InvariantCulture);

        command.Parameters.Add("@error", SqlDbType.NVarChar, -1).Value =
            (object?)error ?? DBNull.Value;

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static object JsonPrimaryKeyValue(ColumnInfo column, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        if (column.DataType.Equals("binary", StringComparison.OrdinalIgnoreCase) ||
            column.DataType.Equals("varbinary", StringComparison.OrdinalIgnoreCase) ||
            column.DataType.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromBase64String(value.GetString() ?? string.Empty);
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private static string AddSelectionPredicate(
        SqlCommand command,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        PrimaryKeySelectionPart selection,
        int selectionIndex)
    {
        if (!selection.IsRange)
        {
            var comparisons = new string[primaryKeyColumns.Count];

            for (var keyIndex = 0; keyIndex < primaryKeyColumns.Count; keyIndex++)
            {
                var parameterName = $"@s{selectionIndex}k{keyIndex}";
                AddRawPrimaryKeyParameter(
                    command,
                    parameterName,
                    primaryKeyColumns[keyIndex],
                    selection.Start.Values[keyIndex]);

                comparisons[keyIndex] =
                    $"{QuoteIdentifier(primaryKeyColumns[keyIndex].Name)} = {parameterName}";
            }

            return $"({string.Join(" AND ", comparisons)})";
        }

        var predicates = new List<string>(primaryKeyColumns.Count);

        for (var keyIndex = 0; keyIndex < primaryKeyColumns.Count - 1; keyIndex++)
        {
            var parameterName = $"@s{selectionIndex}k{keyIndex}";
            AddRawPrimaryKeyParameter(
                command,
                parameterName,
                primaryKeyColumns[keyIndex],
                selection.Start.Values[keyIndex]);

            predicates.Add(
                $"{QuoteIdentifier(primaryKeyColumns[keyIndex].Name)} = {parameterName}");
        }

        var lastIndex = primaryKeyColumns.Count - 1;
        var startParameter = $"@s{selectionIndex}Start";
        var endParameter = $"@s{selectionIndex}End";

        AddRawPrimaryKeyParameter(
            command,
            startParameter,
            primaryKeyColumns[lastIndex],
            selection.Start.Values[lastIndex]);
        AddRawPrimaryKeyParameter(
            command,
            endParameter,
            primaryKeyColumns[lastIndex],
            selection.End!.Values[lastIndex]);

        predicates.Add(
            $"{QuoteIdentifier(primaryKeyColumns[lastIndex].Name)} BETWEEN {startParameter} AND {endParameter}");

        return $"({string.Join(" AND ", predicates)})";
    }

    /// <summary>
    /// Los valores se envían como texto para que la conversión y sus posibles errores
    /// ocurran en SQL Server, no en validaciones locales de C#.
    /// </summary>
    private static void AddRawPrimaryKeyParameter(
        SqlCommand command,
        string name,
        ColumnInfo primaryKey,
        string rawValue)
    {
        var sqlType = primaryKey.DataType.ToLowerInvariant() switch
        {
            "char" => SqlDbType.Char,
            "varchar" or "text" => SqlDbType.VarChar,
            "nchar" => SqlDbType.NChar,
            _ => SqlDbType.NVarChar
        };

        command.Parameters.Add(name, sqlType, Math.Max(rawValue.Length, 1)).Value = rawValue;
    }

    private static PrimaryKeyValue ReadPrimaryKey(
        SqlDataReader reader,
        int columnCount)
    {
        var values = new string[columnCount];

        for (var index = 0; index < columnCount; index++)
        {
            values[index] = reader.IsDBNull(index)
                ? "NULL"
                : FormatSqlValue(reader.GetValue(index));
        }

        return new PrimaryKeyValue(values);
    }

    private static string FormatSqlValue(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static void WriteSqlValue(
        Utf8JsonWriter writer,
        string propertyName,
        object value)
    {
        writer.WritePropertyName(propertyName);

        switch (value)
        {
            case DBNull:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string? NormalizeDocumentUrl(string? document)
    {
        if (document is null)
        {
            return null;
        }

        const string baseUrl = "https://docudigital.ssffaa.cl/";

        return baseUrl + document
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // No se oculta la excepción original si el borrado de limpieza falla.
        }
    }

    private static string SafeDbObjectName(string value)
    {
        if (value.Length <= 128)
        {
            return value;
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
        return $"{value[..115]}_{hash}";
    }

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private async Task<SqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_builder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static (string Schema, string Table) SplitTableName(string tableName)
    {
        var separator = tableName.IndexOf('.');

        return separator < 0
            ? ("dbo", tableName)
            : (tableName[..separator], tableName[(separator + 1)..]);
    }

    private void EnsureDatabaseSelected()
    {
        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("No hay una base de datos seleccionada.");
        }
    }
}
