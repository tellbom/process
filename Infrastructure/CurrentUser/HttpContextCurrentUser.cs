using FlowableWrapper.Domain.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace FlowableWrapper.Infrastructure.CurrentUser
{
    /// <summary>
    /// Reads the current user id from the client-provided auth token.
    /// Development-only behavior: the token is trusted and not validated.
    /// </summary>
    public class HttpContextCurrentUser : ICurrentUser
    {
        private const string UserIdClaimName = "userid";

        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string UserId
        {
            get
            {
                var context = _httpContextAccessor.HttpContext;
                if (context == null) return null!;

                var token = ReadAuthToken(context.Request);
                return string.IsNullOrWhiteSpace(token)
                    ? null!
                    : ReadUserIdFromTrustedToken(token);
            }
        }

        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(UserId);

        private static string? ReadAuthToken(HttpRequest request)
        {
            var  token = request.Headers["Authorization"].FirstOrDefault();

            const string bearerPrefix = "Bearer ";
            return token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? token[bearerPrefix.Length..].Trim()
                : token.Trim();
        }

        private static string ReadUserIdFromTrustedToken(string token)
        {
            if (TryReadUserIdFromJson(token, out var userId))
                return userId;

            var tokenParts = token.Split('.');
            if (tokenParts.Length >= 2 &&
                TryDecodeBase64Url(tokenParts[1], out var jwtPayload) &&
                TryReadUserIdFromJson(jwtPayload, out userId))
            {
                return userId;
            }

            if (TryDecodeBase64Url(token, out var payload) &&
                TryReadUserIdFromJson(payload, out userId))
            {
                return userId;
            }

            return null!;
        }

        private static bool TryReadUserIdFromJson(string json, [NotNullWhen(true)] out string? userId)
        {
            userId = null;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(UserIdClaimName, out var userIdElement))
                    return false;

                userId = userIdElement.ValueKind == JsonValueKind.String
                    ? userIdElement.GetString()
                    : userIdElement.GetRawText();

                return !string.IsNullOrWhiteSpace(userId);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryDecodeBase64Url(string value, [NotNullWhen(true)] out string? decoded)
        {
            decoded = null;

            try
            {
                var base64 = value.Replace('-', '+').Replace('_', '/');
                var padding = base64.Length % 4;
                if (padding > 0)
                    base64 = base64.PadRight(base64.Length + 4 - padding, '=');

                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
