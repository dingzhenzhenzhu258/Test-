using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SerialPortService.Services;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Handler;

namespace SerialPortService.Extensions
{
    /// <summary>
    /// <c>SerialPortService</c> 依赖注入扩展。
    /// 提供默认注册与基于配置绑定注册两种入口。
    /// </summary>
    public static class SerialPortServiceExtensions
    {
        /// <summary>
        /// 使用默认 <see cref="GenericHandlerOptions"/> 注册串口服务。
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <returns>服务集合（链式调用）</returns>
        public static IServiceCollection AddSerialPortService(this IServiceCollection services)
        {
            // 步骤1：校验依赖注入容器参数。
            // 为什么：扩展方法属于应用启动阶段，空引用会导致启动失败。
            // 风险点：若 services 为空会触发空引用异常，影响宿主进程启动。
            ArgumentNullException.ThrowIfNull(services);

            // 步骤2：注册默认运行参数。
            // 为什么：保证即使没有外部配置，也有一组可运行默认值。
            // 风险点：未注册 options 时，服务构造可能拿不到配置导致行为不可预测。
            services.TryAddSingleton(new GenericHandlerOptions());

            // 步骤3：注册串口服务实现。
            // 为什么：业务层通过接口解耦具体实现。
            // 风险点：重复注册若不控制，会出现覆盖或生命周期混乱问题。
            services.TryAddSingleton<ISerialPortService, SerialPortServiceBase>();
            return services;
        }

        /// <summary>
        /// 从配置中读取 <see cref="GenericHandlerOptions"/> 相关选项时使用的节路径。
        /// </summary>
        private const string GenericHandlerOptionsSection = "SerialPortService:GenericHandlerOptions";

        /// <summary>
        /// 基于配置注册串口服务，并对关键参数执行安全归一化。
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="configuration">应用配置</param>
        /// <returns>服务集合（链式调用）</returns>
        public static IServiceCollection AddSerialPortService(this IServiceCollection services, IConfiguration configuration)
        {
            // 步骤1：校验入参。
            // 为什么：配置绑定发生在启动阶段，参数必须有效。
            // 风险点：空配置对象会导致绑定异常并中断应用启动。
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            // 步骤2：读取原始配置与默认值。
            // 为什么：需要在绑定后进行归一化兜底。
            // 风险点：直接使用原始配置会把非法值传入运行期。
            var defaults = new GenericHandlerOptions();
            var rawOptions = new GenericHandlerOptions();
            configuration.GetSection("SerialPortService:GenericHandlerOptions").Bind(rawOptions);

            // 步骤3：归一化配置。
            // 为什么：统一把非法/越界值回退到默认值，降低运行时故障概率。
            // 风险点：不归一化会在高并发场景触发容量/阈值类参数异常。
            var options = new GenericHandlerOptions
            {
                ResponseChannelCapacity = rawOptions.ResponseChannelCapacity > 0 ? rawOptions.ResponseChannelCapacity : defaults.ResponseChannelCapacity,
                SampleLogInterval = rawOptions.SampleLogInterval >= 0 ? rawOptions.SampleLogInterval : defaults.SampleLogInterval,
                DropWhenNoActiveRequest = rawOptions.DropWhenNoActiveRequest,
                ResponseChannelFullMode = rawOptions.ResponseChannelFullMode,
                WaitModeQueueCapacity = rawOptions.WaitModeQueueCapacity > 0 ? rawOptions.WaitModeQueueCapacity : defaults.WaitModeQueueCapacity,
                ProtocolTag = rawOptions.ProtocolTag,
                DeviceTypeTag = rawOptions.DeviceTypeTag,
                ReconnectIntervalMs = rawOptions.ReconnectIntervalMs > 0 ? rawOptions.ReconnectIntervalMs : defaults.ReconnectIntervalMs,
                MaxReconnectAttempts = rawOptions.MaxReconnectAttempts > 0 ? rawOptions.MaxReconnectAttempts : defaults.MaxReconnectAttempts,
                TimeoutRateAlertThresholdPercent = rawOptions.TimeoutRateAlertThresholdPercent is >= 0 and <= 100
                    ? rawOptions.TimeoutRateAlertThresholdPercent
                    : defaults.TimeoutRateAlertThresholdPercent,
                TimeoutRateAlertMinSamples = rawOptions.TimeoutRateAlertMinSamples > 0
                    ? rawOptions.TimeoutRateAlertMinSamples
                    : defaults.TimeoutRateAlertMinSamples,
                WaitBacklogAlertThreshold = rawOptions.WaitBacklogAlertThreshold >= 0
                    ? rawOptions.WaitBacklogAlertThreshold
                    : defaults.WaitBacklogAlertThreshold,
                ReconnectFailureRateAlertThresholdPercent = rawOptions.ReconnectFailureRateAlertThresholdPercent is >= 0 and <= 100
                    ? rawOptions.ReconnectFailureRateAlertThresholdPercent
                    : defaults.ReconnectFailureRateAlertThresholdPercent,
                ReconnectFailureRateAlertMinSamples = rawOptions.ReconnectFailureRateAlertMinSamples > 0
                    ? rawOptions.ReconnectFailureRateAlertMinSamples
                    : defaults.ReconnectFailureRateAlertMinSamples
            };

            // 步骤4：覆盖已注册 options。
            // 为什么：显式配置应优先于默认注册。
            // 风险点：若仅 TryAdd，会导致“配置存在但不生效”的隐蔽问题。
            services.Replace(ServiceDescriptor.Singleton(options));

            // 步骤5：注册串口服务实现。
            // 为什么：确保调用方通过接口可解析到统一服务。
            // 风险点：未注册时业务侧构造会失败。
            services.TryAddSingleton<ISerialPortService, SerialPortServiceBase>();
            return services;
        }
    }
}
