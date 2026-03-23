using Logger.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Logger.Extensions.WebAPI
{
    /// <summary>
    /// HTTP 请求/响应日志中间件。
    /// 默认仅记录方法、路径、耗时和状态码；请求/响应体需要显式配置后才会记录。
    /// </summary>
    public class RequestResponseLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestResponseLoggingMiddleware> _logger;
        private readonly IConfiguration _configuration;

        private const int DefaultMaxBodyLogBytes = 4096;

        public RequestResponseLoggingMiddleware(
            RequestDelegate next,
            ILogger<RequestResponseLoggingMiddleware> logger,
            IConfiguration configuration)
        {
            _next = next;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var logRequestBody = GetBoolean("Logger:WebApi:LogRequestBody", false);
            var logResponseBody = GetBoolean("Logger:WebApi:LogResponseBody", false);
            var maxBodyLogBytes = Math.Max(1, GetInt32("Logger:WebApi:MaxBodyLogBytes", DefaultMaxBodyLogBytes));

            var requestBody = logRequestBody
                ? await FormatRequestAsync(context.Request, maxBodyLogBytes).ConfigureAwait(false)
                : "(Disabled)";

            var originalBodyStream = context.Response.Body;
            MemoryStream? memoryStream = null;
            if (logResponseBody)
            {
                memoryStream = new MemoryStream();
                context.Response.Body = memoryStream;
            }

            var watch = Stopwatch.StartNew();

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                watch.Stop();

                var responseBody = logResponseBody && memoryStream != null
                    ? await FormatResponseAsync(context.Response, maxBodyLogBytes).ConfigureAwait(false)
                    : "(Disabled)";

                _logger.AddLog(
                    LogLevel.Information,
                    string.Concat(
                        "【HTTP 交互记录】 ", context.Request.Method, " ", context.Request.Path,
                        " | 耗时: ", watch.ElapsedMilliseconds, "ms | 状态码: ", context.Response.StatusCode, " \n",
                        "[Request Body] : ", requestBody, "\n",
                        "[Response Body]: ", responseBody));

                if (memoryStream != null)
                {
                    context.Response.Body = originalBodyStream;
                    memoryStream.Position = 0;
                    await memoryStream.CopyToAsync(originalBodyStream).ConfigureAwait(false);
                    await memoryStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<string> FormatRequestAsync(HttpRequest request, int maxBodyLogBytes)
        {
            var effectiveLength = request.ContentLength;
            if (!effectiveLength.HasValue && request.Body.CanSeek)
            {
                effectiveLength = request.Body.Length;
            }

            if (!CanLogBody(request.ContentType, effectiveLength, maxBodyLogBytes))
            {
                return BuildBodySkipReason(request.ContentType, effectiveLength, maxBodyLogBytes);
            }

            request.EnableBuffering();
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            request.Body.Position = 0;

            if (Encoding.UTF8.GetByteCount(body) > maxBodyLogBytes)
            {
                return BuildBodySkipReason(request.ContentType, Encoding.UTF8.GetByteCount(body), maxBodyLogBytes);
            }

            return string.IsNullOrWhiteSpace(body) ? "(Empty)" : body.Trim();
        }

        private async Task<string> FormatResponseAsync(HttpResponse response, int maxBodyLogBytes)
        {
            var bodyLength = response.ContentLength ?? response.Body.Length;
            if (!CanLogBody(response.ContentType, bodyLength, maxBodyLogBytes))
            {
                return BuildBodySkipReason(response.ContentType, bodyLength, maxBodyLogBytes);
            }

            response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            response.Body.Seek(0, SeekOrigin.Begin);

            if (Encoding.UTF8.GetByteCount(body) > maxBodyLogBytes)
            {
                return BuildBodySkipReason(response.ContentType, Encoding.UTF8.GetByteCount(body), maxBodyLogBytes);
            }

            return string.IsNullOrWhiteSpace(body) ? "(Empty)" : body.Trim();
        }

        private static bool CanLogBody(string? contentType, long? contentLength, int maxBodyLogBytes)
        {
            if (!IsTextLikeContentType(contentType))
            {
                return false;
            }

            if (contentLength.HasValue && contentLength.Value > maxBodyLogBytes)
            {
                return false;
            }

            return true;
        }

        private static bool IsTextLikeContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return true;
            }

            return contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildBodySkipReason(string? contentType, long? contentLength, int maxBodyLogBytes)
        {
            if (!IsTextLikeContentType(contentType))
            {
                return $"(Skipped: content-type={contentType ?? "unknown"})";
            }

            if (contentLength.HasValue && contentLength.Value > maxBodyLogBytes)
            {
                return $"(Skipped: body-too-large length={contentLength.Value}, limit={maxBodyLogBytes})";
            }

            return "(Skipped)";
        }

        private bool GetBoolean(string key, bool defaultValue)
        {
            var raw = _configuration[key];
            return bool.TryParse(raw, out var value) ? value : defaultValue;
        }

        private int GetInt32(string key, int defaultValue)
        {
            var raw = _configuration[key];
            return int.TryParse(raw, out var value) ? value : defaultValue;
        }
    }

    public static class RequestResponseLoggingExtensions
    {
        public static IApplicationBuilder UseRequestResponseLogging(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RequestResponseLoggingMiddleware>();
        }
    }
}
