using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Infrastructure.ElasticSearch;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using process.Domain.Abstractions;

namespace FlowableWrapper.Api.Filters
{
    /// <summary>
    /// 全局异常过滤器
    /// 统一将各类异常映射到标准 HTTP 响应，避免每个 Controller 重复 try-catch
    /// </summary>
    public class GlobalExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<GlobalExceptionFilter> _logger;

        public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger)
        {
            _logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            var ex = context.Exception;

            _logger.LogError(ex, "未处理异常: {Message}", ex.Message);

            var (statusCode, code, message) = ex switch
            {
                BusinessException be
                    => (400, be.Code, be.Message),

                ConcurrentUpdateException
                    => (409, "CONCURRENT_UPDATE", "数据并发冲突，请稍后重试"),

                FlowableApiException fae
                    => (502, "FLOWABLE_ERROR", $"Flowable 引擎错误: {fae.Message}"),

                ArgumentException ae
                    => (400, "INVALID_ARGUMENT", ae.Message),

                _ => (500, "INTERNAL_ERROR", "服务器内部错误，请联系管理员")
            };

            context.Result = new ObjectResult(new ApiErrorResponse
            {
                Code = code,
                Message = message
            })
            { StatusCode = statusCode };

            context.ExceptionHandled = true;
        }
    }

    /// <summary>
    /// 标准错误响应体
    /// </summary>
    public class ApiErrorResponse
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }
}
