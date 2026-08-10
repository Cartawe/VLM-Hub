using MySqlConnector;
using VlmHub.MySQL.Models;

namespace VlmHub.MySQL;

/// <summary>
/// Encapsula la conexión y las consultas de catálogo necesarias para navegar
/// por un servidor MySQL.
/// </summary>
public sealed class MySqlServer
{
    private readonly MySqlConnectionStringBuilder _builder;

    public string ServerHost => _builder.Server;
    public string User => _builder.UserID;
    public string? Database { get; private set; }

    public MySqlServer(string serverHost, string user, string password)
    {
        _builder = new MySqlConnectionStringBuilder
        {
            Server = serverHost,
            UserID = user,
            Password = password,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30
        };
    }

    /// <summary>
    /// Cambia la base de datos que utilizarán las consultas posteriores.
    /// La existencia/permisos se comprueban al ejecutar la siguiente consulta.
    /// </summary>
    public void SetDatabase(string database)
    {
        Database = database;
        _builder.Database = database;
    }

    /// <summary>
    /// Devuelve únicamente las bases de datos visibles para el usuario conectado.
    /// Esta consulta también sirve para comprobar que las credenciales permiten
    /// conectarse al servidor.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand("SHOW DATABASES;", connection);
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
    /// Las vistas quedan fuera porque VLMHub procesará tablas.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTablesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @database
              AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@database", Database);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tables = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString("TABLE_NAME"));
        }

        return tables;
    }

    /// <summary>
    /// Obtiene la información mínima necesaria de las columnas, incluyendo
    /// la posición dentro de una PK simple o compuesta.
    /// </summary>
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseSelected();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.COLUMN_TYPE,
                c.ORDINAL_POSITION,
                c.IS_NULLABLE,
                pk.ORDINAL_POSITION AS PK_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pk
                ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA
               AND pk.TABLE_NAME = c.TABLE_NAME
               AND pk.COLUMN_NAME = c.COLUMN_NAME
               AND pk.CONSTRAINT_NAME = 'PRIMARY'
            WHERE c.TABLE_SCHEMA = @database
              AND c.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@database", Database);
        command.Parameters.AddWithValue("@table", tableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<ColumnInfo>();
        var pkPositionOrdinal = reader.GetOrdinal("PK_POSITION");

        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString("COLUMN_NAME"),
                DataType = reader.GetString("DATA_TYPE"),
                ColumnType = reader.GetString("COLUMN_TYPE"),
                Position = reader.GetInt32("ORDINAL_POSITION"),
                IsNullable = string.Equals(
                    reader.GetString("IS_NULLABLE"),
                    "YES",
                    StringComparison.OrdinalIgnoreCase),
                PrimaryKeyPosition = reader.IsDBNull(pkPositionOrdinal)
                    ? null
                    : reader.GetInt32(pkPositionOrdinal)
            });
        }

        return columns;
    }

    private async Task<MySqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_builder.ConnectionString);

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

    private void EnsureDatabaseSelected()
    {
        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("No hay una base de datos seleccionada.");
        }
    }
}
