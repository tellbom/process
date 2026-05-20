using FlowableWrapper.Domain.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FlowableWrapper.Infrastructure.CurrentUser
{
    /// <summary>
    /// 基于 HttpContext 的当前用户实现
    /// 当前阶段从请求头 X-User-Id 或 Query 参数 userId 读取
    ///
    /// TODO: Phase N — 对接 Keycloak JWT 后，改为从 ClaimsPrincipal 读取
    ///       届时替换此实现即可，ICurrentUser 接口不变
    /// </summary>
    public class HttpContextCurrentUser : ICurrentUser
    {
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
                if (context == null) return null;

                // 优先从请求头读取（前端在 Header 中传 X-User-Id）
                var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(userId))
                    return userId;

                // 其次从 Query 参数读取（兼容测试场景）
                userId = context.Request.Query["userId"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(userId))
                    return userId;

                // TODO: Keycloak JWT 接入后从此处读取 sub claim
                // var claim = context.User?.FindFirst(ClaimTypes.NameIdentifier);
                // return claim?.Value;

                return null;
            }
        }

        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(UserId);
    }
}
