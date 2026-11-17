using System.Text.Json;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.Reliability;

namespace FlowableWrapper.Application.Services;

internal static class WorkflowBusinessListCursor
{
    // Preserve DM8 timestamp precision; the ES cursor only preserves milliseconds.
    internal static string Encode(WorkflowBusinessInstance last)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new object[] { "dm8-v1", last.CreatedAt.Ticks, last.ProcessInstanceId! }));

    internal static (DateTime? CreatedAt, string? InstanceId) Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            using var json = JsonDocument.Parse(Convert.FromBase64String(cursor));
            var values = json.RootElement;
            if (values.GetArrayLength() != 3 || values[0].GetString() != "dm8-v1"
                || string.IsNullOrWhiteSpace(values[2].GetString())) throw new FormatException();
            return (new DateTime(values[1].GetInt64()), values[2].GetString());
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new BusinessException("分页游标无效，请刷新列表", "INVALID_CURSOR");
        }
    }
}
