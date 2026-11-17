using FlowableWrapper.Domain.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using FlowableWrapper.Configuration;
using System.Security.Claims;

namespace FlowableWrapper.Infrastructure.CurrentUser
{
    /// <summary>
    /// 基于 JWT ClaimsPrincipal 的当前用户实现。
    /// </summary>
    public class HttpContextCurrentUser : ICurrentUser
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly JwtOptions _jwtOptions;

        public HttpContextCurrentUser(
            IHttpContextAccessor httpContextAccessor,
            IOptions<JwtOptions> jwtOptions)
        {
            _httpContextAccessor = httpContextAccessor;
            _jwtOptions = jwtOptions.Value;
        }

        public string UserId
        {
            get
            {
                var context = _httpContextAccessor.HttpContext;
                if (context == null) return null;

                var userId = context.User?.FindFirstValue(_jwtOptions.UseridClaim);
                if (!string.IsNullOrWhiteSpace(userId)) return userId;

                foreach (var claim in _jwtOptions.FallbackUseridClaims)
                {
                    userId = context.User?.FindFirstValue(claim);
                    if (!string.IsNullOrWhiteSpace(userId)) return userId;
                }

                return null;
            }
        }

        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(UserId);
    }
}
