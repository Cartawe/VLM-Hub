namespace VlmHub.MySQL;

public sealed class TableProcess
{
    public string TableName {get; init;}
    public List<string> Columns {get; set;}
    public bool BoolExists {get; set;} = false;
    public string? ColumnDoc {get; private set;}
    public string? PrimaryKey {get; }

}
