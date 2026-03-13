using Microsoft.Extensions.Logging;
using LoggerExtensionsHost = Logger.Extensions.LoggerExtensions;
using Serilog.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Logger.Helpers
{
    /// <summary>
    /// 自定义日志辅助类。
    /// 统一封装日志写入、上下文属性注入与可选 UI 同步输出能力。
    /// </summary>
    public static class LoggerHelper
    {
        // 提供一个事件，供 UI 层订阅
        /// <summary>
        /// UI 日志事件。
        /// 当 <c>isShowUI=true</c> 时触发，便于界面层实时显示日志文本。
        /// </summary>
        public static event Action<LogLevel, string>? OnUILog;

        public static void AddLog(
         this ILogger logger,
         LogLevel level,
         string messageTemplate,
         Exception exception,
         object? args,
         [CallerMemberName] string memberName = "",
         [CallerFilePath] string sourceFilePath = "",
         [CallerLineNumber] int sourceLineNumber = 0)
        {
            var normalizedArgs = args switch
            {
                null => Array.Empty<object>(),
                object[] arrayArgs => arrayArgs,
                _ => new[] { args }
            };

            AddLog(
                logger,
                level,
                messageTemplate,
                isShowUI: false,
                uiMessage: null,
                exception: exception,
                memberName: memberName,
                sourceFilePath: sourceFilePath,
                sourceLineNumber: sourceLineNumber,
                args: normalizedArgs);
        }

        /// <summary>
        /// 统一写日志入口。
        /// 该方法会自动注入调用成员名、文件路径和行号到日志上下文，
        /// 并可选触发 UI 侧日志显示。
        /// </summary>
        /// <param name="logger">日志本体</param>
        /// <param name="level">日志级别</param>
        /// <param name="messageTemplate">日志模板（支持结构化参数）</param>
        /// <param name="isShowUI">是否推送到 UI 事件</param>
        /// <param name="uiMessage">可选 UI 展示文本，不传则使用模板+参数格式化</param>
        /// <param name="exception">异常对象</param>
        /// <param name="memberName">调用成员名（自动填充）</param>
        /// <param name="sourceFilePath">调用文件路径（自动填充）</param>
        /// <param name="sourceLineNumber">调用行号（自动填充）</param>
        /// <param name="args">结构化日志参数</param>
        public static void AddLog(
         this ILogger logger,
         LogLevel level,
         string messageTemplate,
         bool isShowUI = false,
         string? uiMessage = null,
         Exception? exception = null,
         [CallerMemberName] string memberName = "",
         [CallerFilePath] string sourceFilePath = "",
         [CallerLineNumber] int sourceLineNumber = 0,
         params object[] args)
        {
            var safeArgs = args ?? Array.Empty<object>();
            (exception, safeArgs) = NormalizeException(exception, safeArgs);

            if (isShowUI)
            {
                var uiText = uiMessage ?? BuildReplayMessage(messageTemplate, safeArgs);
                OnUILog?.Invoke(level, uiText);
            }

            if (!logger.IsEnabled(level))
            {
                return;
            }

            using (LogContext.PushProperty("MemberName", memberName))
            using (LogContext.PushProperty("FilePath", sourceFilePath))
            using (LogContext.PushProperty("LineNumber", sourceLineNumber))
            {
                using var exceptionCapturedScope = LogContext.PushProperty("ExceptionCaptured", exception != null);
                using var exceptionTypeScope = exception != null
                    ? LogContext.PushProperty("ExceptionType", exception.GetType().FullName ?? exception.GetType().Name)
                    : null;

                logger.Log(level, exception, messageTemplate, safeArgs);

                // 步骤2：当 OTLP 不可用时将业务日志入本地补传队列。
                // 为什么：保障离线窗口内日志不因远端不可达而永久丢失。
                // 风险点：若消息格式化失败会影响补传可读性。
                var replayMessage = BuildReplayMessage(messageTemplate, safeArgs);
                LoggerExtensionsHost.EnqueueReplayLogIfNeeded(level, replayMessage, exception);
            }
        }

        /// <summary>
        /// 构建用于补传队列的日志文本。
        /// </summary>
        private static string BuildReplayMessage(string messageTemplate, object[] args)
        {
            if (args.Length == 0)
            {
                return messageTemplate;
            }

            if (LooksLikeCompositeFormat(messageTemplate))
            {
                try
                {
                    return string.Format(messageTemplate, args);
                }
                catch
                {
                }
            }

            return $"{messageTemplate} | Args: {string.Join(", ", args.Select(a => a?.ToString() ?? "<null>"))}";
        }

        private static bool LooksLikeCompositeFormat(string messageTemplate)
        {
            for (var i = 0; i < messageTemplate.Length - 1; i++)
            {
                if (messageTemplate[i] == '{' && char.IsDigit(messageTemplate[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static (Exception? exception, object[] args) NormalizeException(Exception? exception, object[] args)
        {
            if (exception != null || args.Length == 0)
            {
                return (exception, args);
            }

            for (var i = args.Length - 1; i >= 0; i--)
            {
                if (args[i] is Exception extracted)
                {
                    var newArgs = args.Where((_, index) => index != i).ToArray();
                    return (extracted, newArgs);
                }
            }

            return (exception, args);
        }
    }
}
