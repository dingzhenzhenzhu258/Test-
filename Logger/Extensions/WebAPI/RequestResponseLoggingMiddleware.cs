using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Logger.Extensions.WebAPI
{
    /// <summary>
    /// HTTP 请求/响应日志中间件。
    /// 用于在 WebAPI 场景记录请求体、响应体、状态码与耗时，
    /// 便于线上排障与接口审计。
    /// </summary>
    public class RequestResponseLoggingMiddleware
    {
        /// <summary>
        /// 下一个中间件委托。
        /// </summary>
        private readonly RequestDelegate _next;

        /// <summary>
        /// 当前中间件日志实例。
        /// </summary>
        private readonly ILogger<RequestResponseLoggingMiddleware> _logger;

        /// <summary>
        /// 创建请求响应日志中间件。
        /// </summary>
        public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// 中间件执行入口。
        /// 通过替换 Response.Body 捕获响应内容，最终再回写到原始流。
        /// </summary>
        public async Task InvokeAsync(HttpContext context)
        {
            // 如果是常规的静态文件或心跳检查，可以考虑跳过不记录，节省性能
            // if (context.Request.Path.StartsWithSegments("/health")) { await _next(context); return; }

            // 1. 读取请求体 (Request Body)
            var requestBody = await FormatRequestAsync(context.Request);

            // 2. 准备拦截响应体 (Response Body)
            // 备份原始的响应流
            var originalBodyStream = context.Response.Body;
            // 创建一个新的内存流来接收 Controller 写出的数据
            using var memoryStream = new MemoryStream();
            context.Response.Body = memoryStream;

            // 记录一下耗时
            var watch = Stopwatch.StartNew();

            try
            {
                // 放行请求，让 Controller 去处理业务逻辑
                await _next(context);
            }
            finally
            {
                watch.Stop();

                // 3. 读取响应体 (Response Body)
                var responseBody = await FormatResponseAsync(context.Response);

                // 4. 打包输出到日志 (发送给 OpenObserve)
                _logger.AddLog(
                    LogLevel.Information,
                    string.Concat(
                        "【HTTP 交互记录】 ", context.Request.Method, " ", context.Request.Path,
                        " | 耗时: ", watch.ElapsedMilliseconds, "ms | 状态码: ", context.Response.StatusCode, " \n",
                        "[Request Body] : ", requestBody, "\n",
                        "[Response Body]: ", responseBody));

                // 5. 【极其关键】把我们内存流里的数据，写回到真正的原始响应流中发给客户端
                await memoryStream.CopyToAsync(originalBodyStream);
            }
        }

        /// <summary>
        /// 读取并格式化请求体。
        /// </summary>
        private async Task<string> FormatRequestAsync(HttpRequest request)
        {
            // 开启缓冲，允许流被多次读取
            request.EnableBuffering();

            // leaveOpen: true 保证 StreamReader 释放时，不要关掉底层的 request.Body
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            // 读完之后，必须把指针拨回头部，否则后面的 Controller 读到的就是空！
            request.Body.Position = 0;

            return string.IsNullOrWhiteSpace(body) ? "(Empty)" : body.Trim();
        }

        /// <summary>
        /// 读取并格式化响应体。
        /// </summary>
        private async Task<string> FormatResponseAsync(HttpResponse response)
        {
            // Controller 写完数据后，流的指针在最末尾，我们需要把它拨回头部才能读
            response.Body.Seek(0, SeekOrigin.Begin);

            using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

            // 读完之后，再次把指针拨回头部，方便后续 CopyToAsync
            response.Body.Seek(0, SeekOrigin.Begin);

            return string.IsNullOrWhiteSpace(body) ? "(Empty)" : body.Trim();
        }
    }

    /// <summary>
    /// 请求响应日志中间件扩展。
    /// 用于以扩展方法方式注册 <see cref="RequestResponseLoggingMiddleware"/>。
    /// </summary>
    public static class RequestResponseLoggingExtensions
    {
        /// <summary>
        /// 启用请求响应日志中间件。
        /// </summary>
        public static IApplicationBuilder UseRequestResponseLogging(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RequestResponseLoggingMiddleware>();
        }
    }
}
