using System.Text;
using System.Text.Json;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;

namespace FlowableWrapper.Application.Services;

public static class ProcessListCursor
{
    public static string Encode(ProcessMetadataDocument last)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[]
        {
            new DateTimeOffset(DateTime.SpecifyKind(last.CreatedTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            last.ProcessInstanceId
        })));

    public static object[]? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            using var json = JsonDocument.Parse(Convert.FromBase64String(cursor));
            var values = json.RootElement;
            if (values.GetArrayLength() != 2 || string.IsNullOrWhiteSpace(values[1].GetString()))
                throw new FormatException();
            return new object[] { values[0].GetInt64(), values[1].GetString()! };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new BusinessException("分页游标无效，请刷新列表", "INVALID_CURSOR");
        }
    }
}
