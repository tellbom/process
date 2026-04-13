using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Flowable
{
    /// <summary>
    /// Flowable REST API 底层 HTTP 客户端
    /// 封装认证、序列化、错误处理，供各 Service 实现使用
    /// </summary>
    public class FlowableHttpClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<FlowableHttpClient> _logger;
        private readonly FlowableOptions _options;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public FlowableHttpClient(
            HttpClient http,
            IOptions<FlowableOptions> options,
            ILogger<FlowableHttpClient> logger)
        {
            _options = options.Value;
            _logger = logger;
            _http = http;
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

            // Basic Auth
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_options.Username}:{_options.Password}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            _http.DefaultRequestHeaders.Accept
                .Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<T> GetAsync<T>(string path)
        {
            _logger.LogDebug("Flowable GET {Path}", path);
            var response = await _http.GetAsync(path);
            await EnsureSuccessAsync(response, path);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }

        public async Task<T> PostAsync<T>(string path, object body)
        {
            _logger.LogDebug("Flowable POST {Path}", path);
            var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(path, content);
            await EnsureSuccessAsync(response, path);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }

        public async Task PostAsync(string path, object body)
        {
            _logger.LogDebug("Flowable POST {Path}", path);
            var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(path, content);
            await EnsureSuccessAsync(response, path);
        }

        public async Task<T> PostMultipartAsync<T>(string path, MultipartFormDataContent content)
        {
            _logger.LogDebug("Flowable POST multipart {Path}", path);
            var response = await _http.PostAsync(path, content);
            await EnsureSuccessAsync(response, path);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }

        public async Task DeleteAsync(string path)
        {
            _logger.LogDebug("Flowable DELETE {Path}", path);
            var response = await _http.DeleteAsync(path);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return; // 幂等，不存在视为成功
            await EnsureSuccessAsync(response, path);
        }

        private async Task EnsureSuccessAsync(HttpResponseMessage response, string path)
        {
            if (response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Flowable API 错误: {StatusCode} {Path} {Body}",
                response.StatusCode, path, body);
            throw new FlowableApiException(
                $"Flowable API 调用失败: {(int)response.StatusCode} {path} — {body}",
                (int)response.StatusCode);
        }
    }

    /// <summary>
    /// Flowable API 调用异常
    /// </summary>
    public class FlowableApiException : Exception
    {
        public int StatusCode { get; }

        public FlowableApiException(string message, int statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
