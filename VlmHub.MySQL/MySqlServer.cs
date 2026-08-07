using MySqlConnector;
using System.Data;

namespace VlmHub.MySQL;

public sealed class MySqlServer
{
    public string? ServerName {get; set;}
    public string ServerHost {get; init;}
    public string User {get; init;}
    private readonly string _psswd;
    public string? DataBase {get; private set;}
    private MySqlConnectionStringBuilder Builder{get; set;}

    public MySqlServer(string serverHost, string user, string psswd)
    {
        ServerHost = serverHost;
        User = user;
        _psswd = psswd;
        Builder = new MySqlConnectionStringBuilder
        {
            Server = ServerHost,
            UserID = User,
            Password = _psswd
        };

    }

    public void SetDatabase(string database)
    {
        DataBase = database;
        Builder = new MySqlConnectionStringBuilder
        {
            Server = ServerHost,
            UserID = User,
            Password = _psswd,
            Database = DataBase
        };
    }

    internal async Task<MySqlConnection> TryOpenConnection()
    {
        var connection = new MySqlConnection(Builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("Conexión rechazada");
        }
    }

    public async Task<List<string>> GetDatabasesAsync()
    {
        await using var connection = await TryOpenConnection();

        var databases = new List<string>();
        string sql = "SHOW DATABASES;";
        var command = new MySqlCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            databases.Add(reader.GetString(0));
        }
        return databases;
    }

    public async Task<List<string>> GetTablesAsync()
    {
        if (DataBase == null)
        {
            throw new InvalidOperationException("No hay base de datos seleccionada");
        }

        await using var connection = await TryOpenConnection();

        var tables = new List<string>();
        string sql = "SHOW TABLES";
        var command = new MySqlCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

}

