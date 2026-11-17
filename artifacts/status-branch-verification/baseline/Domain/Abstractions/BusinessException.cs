namespace FlowableWrapper.Domain.Abstractions
{
    /// <summary>
    /// 业务异常，用于可预期的业务规则校验失败
    /// Controller 层统一捕获并返回 400
    /// </summary>
    public class BusinessException : Exception
    {
        public string Code { get; }

        public BusinessException(string message, string code = "BUSINESS_ERROR")
            : base(message)
        {
            Code = code;
        }
    }
}
