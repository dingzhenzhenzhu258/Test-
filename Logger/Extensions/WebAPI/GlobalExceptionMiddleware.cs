using Logger.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace Logger.Extensions.WebAPI
{
    /// <summary>
    /// WebAPI 全局异常中间件。
    /// 统一捕获未处理异常并返回标准化 JSON 响应，同时将异常写入统一日志管道。
    /// </summary>
    public class GlobalExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionMiddleware> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment? _hostEnvironment;

        public GlobalExceptionMiddleware(
            RequestDelegate next,
            ILogger<GlobalExceptionMiddleware> logger,
            IConfiguration configuration,
            IHostEnvironment? hostEnvironment = null)
        {
            _next = next;
            _logger = logger;
            _configuration = configuration;
            _hostEnvironment = hostEnvironment;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.AddLog(
                    LogLevel.Error,
                    string.Concat("WebAPI 发生未处理的全局异常: ", ex.Message),
                    exception: ex);

                await HandleExceptionAsync(context, ex, ShouldIncludeExceptionDetail()).ConfigureAwait(false);
            }
        }

        private static Task HandleExceptionAsync(HttpContext context, Exception exception, bool includeExceptionDetail)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            object response = includeExceptionDetail
                ? new
                {
                    StatusCode = context.Response.StatusCode,
                    Message = "服务器内部发生错误，请稍后重试或联系管理员。",
                    ErrorDetail = exception.Message
                }
                : new
                {
                    StatusCode = context.Response.StatusCode,
                    Message = "服务器内部发生错误，请稍后重试或联系管理员。"
                };

            return context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }

        private bool ShouldIncludeExceptionDetail()
        {
            var configured = _configuration["Logger:WebApi:IncludeExceptionDetails"];
            if (bool.TryParse(configured, out var includeExceptionDetail))
            {
                return includeExceptionDetail;
            }

            return _hostEnvironment?.IsDevelopment() == true;
        }
    }

    public static class GlobalExceptionMiddlewareExtensions
    {
        public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<GlobalExceptionMiddleware>();
        }
    }
}
