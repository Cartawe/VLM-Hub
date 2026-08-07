using MySqlConnector;

namespace VlmHub.MySQL;

public sealed class PrimaryKeyService
{
    private readonly MySqlServer _server;

    public PrimaryKeyService(MySqlServer server)
    {
        _server = server;
    }

    public async Task<List<PrimaryKeyColumn>> GetAsync(string table)
    {
        if (_server.DataBase is null)
        {
            throw new InvalidOperationException(
                "No hay base de datos seleccionada.");
        }

        await using var connection = await _server.TryOpenConnection();

        const string sql = """
            SELECT
                k.COLUMN_NAME,
                k.ORDINAL_POSITION,
                c.DATA_TYPE,
                c.COLUMN_TYPE
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            JOIN INFORMATION_SCHEMA.COLUMNS c
                ON c.TABLE_SCHEMA = k.TABLE_SCHEMA
                AND c.TABLE_NAME = k.TABLE_NAME
                AND c.COLUMN_NAME = k.COLUMN_NAME
            WHERE k.TABLE_SCHEMA = @database
              AND k.TABLE_NAME = @table
              AND k.CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY k.ORDINAL_POSITION;
            """;

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@database", _server.DataBase);

        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<PrimaryKeyColumn>();

        while (await reader.ReadAsync())
        {
            columns.Add(new PrimaryKeyColumn
            {
                Name = reader.GetString("COLUMN_NAME"),
                DataType = reader.GetString("DATA_TYPE"),
                ColumnType = reader.GetString("COLUMN_TYPE"),
                Position = reader.GetInt32("ORDINAL_POSITION")
            });
        }

        return columns;
    }
}

public sealed class PrimaryKeyColumn
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required string ColumnType { get; init; }
    public int Position { get; init; }
}