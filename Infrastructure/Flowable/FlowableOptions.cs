namespace FlowableWrapper.Infrastructure.Flowable
{
    /// <summary>
    /// Flowable REST API 连接配置
    /// 绑定到 appsettings.json 的 Flowable 节点
    /// </summary>
    public class FlowableOptions
    {
        /// <summary>
        /// Flowable REST API 根地址
        /// 示例：http://localhost:8080/flowable-rest/service
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// 认证用户名（Flowable 默认 admin）
        /// </summary>
        public string Username { get; set; } = "admin";

        /// <summary>
        /// 认证密码（Flowable 默认 test）
        /// </summary>
        public string Password { get; set; } = "test";

        /// <summary>
        /// HTTP 请求超时秒数，默认 30
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 框架回调 URL（注入到流程变量 frameworkCallbackUrl）
        /// Flowable HTTP Task 完成后会 POST 此地址
        /// 示例：https://my-workflow-center/api/callback/flowable
        /// </summary>
        public string FrameworkCallbackUrl { get; set; }
    }
}
