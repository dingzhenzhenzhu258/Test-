using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Logger.Extensions.WebAPI
{
    /*
     var app = builder.Build();

        // ==========================================
        // 1. 最外层：全局异常捕获（兜底防线，拦截 500 错误并转换格式）
        app.UseGlobalExceptionHandler();

        // 2. 第二层：输入输出数据包记录（这样即使后面报错了，这里也能完整记录发了什么，返回了什么 500 信息）
        app.UseRequestResponseLogging();
        // ==========================================

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
     */

    /// <summary>
    /// WebAPI 全局异常中间件。
    /// 统一捕获未处理异常并返回标准化 JSON 响应，
    /// 同时将异常写入统一日志管道。
    /// </summary>
    public class GlobalExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionMiddleware> _logger;

        /// <summary>
        /// 创建全局异常中间件。
        /// </summary>
        public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// 中间件入口。
        /// </summary>
        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // 放行请求，让后续的 Controller 处理
                await _next(context);
            }
            catch (Exception ex)
            {
                // 一旦后续有任何没有 try-catch 的报错，全都会掉进这里
                // 记录错误（Serilog 会自动带着行号和方法名把它推送到云端）
                _logger.AddLog(LogLevel.Error, $"WebAPI 发生未处理的全局异常: {ex.Message}", exception: ex);

                // 统一处理给前端的返回格式
                await HandleExceptionAsync(context, ex);
            }
        }

        /// <summary>
        /// 写入标准错误响应。
        /// </summary>
        private static Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; // 强制返回 500 状态码

            // 构造标准化的返回 JSON
            var response = new
            {
                StatusCode = context.Response.StatusCode,
                Message = "服务器内部发生错误，请稍后重试或联系管理员。",
                // 提示：生产环境下最好把下面这行注释掉，避免暴露底层代码细节给外部用户
                ErrorDetail = exception.Message
            };

            return context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
    }

    // 提供一个美观的扩展方法
    public static class GlobalExceptionMiddlewareExtensions
    {
        /// <summary>
        /// 启用全局异常处理中间件。
        /// </summary>
        public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<GlobalExceptionMiddleware>();
        }
    }
}
