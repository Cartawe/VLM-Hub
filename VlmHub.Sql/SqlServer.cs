using Microsoft.Data.SqlClient;
using VlmHub.Sql.Models;

namespace VlmHub.Sql;

/// <summary>
/// Encapsula la conexión y las consultas de catálogo necesarias para navegar
/// por un servidor SQL Server.
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

            // Útil para servidores internos que usan certificados autofirmados.
            // La conexión puede seguir cifrada, pero no se valida la cadena del certificado.
            TrustServerCertificate = true
        };
    }

    /// <summary>
    /// Cambia la base de datos que utilizarán las consultas posteriores.
    /// La existencia y los permisos se comprueban al abrir la siguiente conexión.
    /// </summary>
    public void SetDatabase(string database)
    {
        Database = database;
        _builder.InitialCatalog = database;
    }

    /// <summary>
    /// Devuelve únicamente las bases de datos accesibles para el usuario conectado.
    /// Esta consulta también sirve para comprobar que las credenciales permiten
    /// conectarse al servidor.
    /// </summary>
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

    /// <summary>
    /// Devuelve las tablas físicas de la base de datos seleccionada.
    /// Se incluye el schema (por ejemplo, dbo.Documentos) para evitar ambigüedades.
    /// Las vistas y tablas internas del sistema quedan fuera.
    /// </summary>
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
            var schema = reader.GetString(schemaOrdinal);
            var table = reader.GetString(tableOrdinal);

            tables.Add($"{schema}.{table}");
        }

        return tables;
    }

    /// <summary>
    /// Obtiene la información mínima necesaria de las columnas, incluyendo
    /// la posición dentro de una clave primaria simple o compuesta.
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
        command.Parameters.Add("@schema", System.Data.SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@table", System.Data.SqlDbType.NVarChar, 128).Value = table;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<ColumnInfo>();

        var nameOrdinal = reader.GetOrdinal("COLUMN_NAME");
        var dataTypeOrdinal = reader.GetOrdinal("DATA_TYPE");
        var columnTypeOrdinal = reader.GetOrdinal("COLUMN_TYPE");
        var positionOrdinal = reader.GetOrdinal("ORDINAL_POSITION");
        var nullableOrdinal = reader.GetOrdinal("IS_NULLABLE");
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
                PrimaryKeyPosition = reader.IsDBNull(pkPositionOrdinal)
                    ? null
                    : Convert.ToInt32(reader.GetValue(pkPositionOrdinal))
            });
        }

        return columns;
    }

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

        if (separator < 0)
        {
            return ("dbo", tableName);
        }

        return (
            tableName[..separator],
            tableName[(separator + 1)..]);
    }

    private void EnsureDatabaseSelected()
    {
        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("No hay una base de datos seleccionada.");
        }
    }
}
