using System.Text.Json;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Infrastructure.Dm8;
using Microsoft.Extensions.Options;
using Dm;
using System.Text.RegularExpressions;

// Read-only verification against the configured DM8 database. No migrations or data writes.
using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[0], "appsettings.json")));
var options = settings.RootElement.GetProperty("Dm8").Deserialize<Dm8Options>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
if (args.Length == 3 && args[1] == "setup")
{
    var schema = args[2];
    if (!Regex.IsMatch(schema, "^REPRO_STATUS_[A-F0-9]{8}$")) throw new ArgumentException("Invalid isolated schema");
    var connectionSettings = new DmConnectionStringBuilder(options.ConnectionString);
    connectionSettings["connPooling"] = false;
    await using var connection = new DmConnection(connectionSettings.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandTimeout = 30;
    command.CommandText = $"CREATE SCHEMA {schema} AUTHORIZATION SYSDBA";
    await command.ExecuteNonQueryAsync();
    command.CommandText = $"SET SCHEMA {schema}";
    await command.ExecuteNonQueryAsync();
    foreach (var migration in Directory.GetFiles(Path.Combine(args[0], "migrations", "dm8"), "00*_up.sql").OrderBy(path => path))
    {
        var sql = Regex.Replace(File.ReadAllText(migration), @"(?m)^\s*--.*$", "");
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }
    Console.WriteLine($"Isolated schema initialized: {schema}");
    return;
}
if (args.Length > 1 && args[1] == "discover")
{
    await using var connection = new DmConnection(options.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandTimeout = 10;
    command.CommandText = "SELECT OWNER, TABLE_NAME FROM ALL_TABLES WHERE TABLE_NAME IN ('WORKFLOW_BUSINESS_INSTANCE', 'WORKFLOW_CALLBACK_EVENT') ORDER BY OWNER, TABLE_NAME";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) Console.WriteLine($"Table location: {reader.GetString(0)}.{reader.GetString(1)}");
    return;
}
if (args.Length > 1) options.Schema = args[1];
var store = new Dm8WorkflowReliabilityStore(Options.Create(options));
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var all = await store.QueryBusinessesAsync(new WorkflowBusinessQuery { Size = 2 }, cancellation.Token);
Console.WriteLine($"DM8 read-only list: total={all.Total}, sample={all.Items.Count}");
foreach (var status in new[] { "running", "completed", "terminated" })
{
    var filtered = await store.QueryBusinessesAsync(new WorkflowBusinessQuery { FlowState = status, Size = 1 }, cancellation.Token);
    if (filtered.Items.Any(item => item.FlowState != status)) throw new Exception("Status filter mismatch");
    Console.WriteLine($"Status filter {status}: total={filtered.Total}, verified");
}
if (all.Items.Count > 0)
{
    var first = all.Items[0];
    var exact = await store.QueryBusinessesAsync(new WorkflowBusinessQuery
    {
        BusinessId = first.BusinessId, BusinessType = first.BusinessType, CreatedBy = first.CreatedBy,
        CreatedTimeFrom = first.CreatedAt, CreatedTimeTo = first.CreatedAt, Size = 100
    }, cancellation.Token);
    if (!exact.Items.Any(item => item.ProcessInstanceId == first.ProcessInstanceId)) throw new Exception("Exact time/owner/business filter mismatch");
    var next = await store.QueryBusinessesAsync(new WorkflowBusinessQuery
    {
        AfterCreatedAt = first.CreatedAt, AfterProcessInstanceId = first.ProcessInstanceId, Size = 2
    }, cancellation.Token);
    if (next.Items.Any(item => item.ProcessInstanceId == first.ProcessInstanceId)) throw new Exception("Cursor repeated its last item");
    Console.WriteLine("Business/owner/time filters and timestamp cursor: verified");
}
