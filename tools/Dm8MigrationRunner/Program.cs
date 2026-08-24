using System.Text.RegularExpressions;
using Dm;

if (args.Length != 2 || !int.TryParse(args[0], out var version))
    throw new ArgumentException("Usage: Dm8MigrationRunner <version> <migration.sql>");

var connectionString = Environment.GetEnvironmentVariable("FLOW_DM8_MIGRATION_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("FLOW_DM8_MIGRATION_CONNECTION_STRING is required.");

await using var connection = new DmConnection(connectionString);
await connection.OpenAsync();
await using (var check = connection.CreateCommand())
{
    check.CommandText = "SELECT COUNT(*) FROM FLOW_RELIABILITY.WORKFLOW_SCHEMA_VERSION WHERE VERSION_NO = :version_no";
    var parameter = check.CreateParameter();
    parameter.ParameterName = "version_no";
    parameter.Value = version;
    check.Parameters.Add(parameter);
    if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0)
    {
        Console.WriteLine($"DM8_SCHEMA_VERSION_{version}=ALREADY_APPLIED");
        return;
    }
}

var sql = await File.ReadAllTextAsync(Path.GetFullPath(args[1]));
var statements = Regex.Replace(sql, @"(?m)^\s*--.*$", string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
foreach (var statement in statements)
{
    await using var command = connection.CreateCommand();
    command.CommandText = statement;
    await command.ExecuteNonQueryAsync();
}

Console.WriteLine($"DM8_SCHEMA_VERSION_{version}=APPLIED");
