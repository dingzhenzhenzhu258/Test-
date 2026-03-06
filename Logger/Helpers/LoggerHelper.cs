using Microsoft.Extensions.Logging;
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
            if (isShowUI)
            {
                var uiText = uiMessage ?? (args != null && args.Length > 0 ? string.Format(messageTemplate, args) : messageTemplate);
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
                logger.Log(level, exception, messageTemplate, args);
            }
        }
    }
}
