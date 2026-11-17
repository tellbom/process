namespace FlowableWrapper.Domain.Abstractions
{
    /// <summary>
    /// 当前登录用户抽象接口
    /// 后续对接 Keycloak JWT 时替换实现即可，调用方不感知
    /// </summary>
    public interface ICurrentUser
    {
        /// <summary>
        /// 用户工号 / UserId
        /// </summary>
        string UserId { get; }

        /// <summary>
        /// 是否已认证
        /// </summary>
        bool IsAuthenticated { get; }
    }
}
