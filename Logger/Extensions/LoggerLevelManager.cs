using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Logger.Extensions
{
    /// <summary>
    /// 日志级别动态管理器。
    /// 通过 <see cref="LogSwitch"/> 控制 Serilog 的最小级别，
    /// 支持运行时在线调节日志输出粒度。
    /// </summary>
    public static class LoggerLevelManager
    {
        // 默认设置为 Information，程序启动时生效
        /// <summary>
        /// 全局日志级别开关。
        /// </summary>
        public static readonly LoggingLevelSwitch LogSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        /// <summary>
        /// 动态设置日志级别。
        /// 可接受值示例：Verbose、Debug、Information、Warning、Error、Fatal。
        /// </summary>
        /// <param name="levelName">目标日志级别名称（忽略大小写）</param>
        public static void SetLevel(string levelName)
        {
            if (Enum.TryParse<LogEventLevel>(levelName, true, out var level))
            {
                LogSwitch.MinimumLevel = level;
            }
        }
    }
}
