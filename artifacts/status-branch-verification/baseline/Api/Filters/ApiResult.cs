namespace FlowableWrapper.Api.Filters
{
    /// <summary>
    /// 统一 API 响应包装
    /// 所有 Controller 返回此结构，前端统一解析
    /// </summary>
    public class ApiResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string Message { get; set; }

        public static ApiResult<T> Ok(T data, string message = null)
            => new() { Success = true, Data = data, Message = message };

        public static ApiResult<object> Fail(string message)
            => new ApiResult<object> { Success = false, Message = message };
    }

    /// <summary>
    /// 无数据的成功响应
    /// </summary>
    public class ApiResult : ApiResult<object>
    {
        public static ApiResult OkEmpty(string message = null)
            => new() { Success = true, Message = message };
    }
}
