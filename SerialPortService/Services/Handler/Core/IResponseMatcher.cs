namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 通用响应匹配策略接口。
    /// 用于把“协议特有匹配逻辑”从 <see cref="GenericHandler{T}"/> 中解耦，
    /// 让新增协议时仅实现策略即可复用并发、限流、重试和指标逻辑。
    /// </summary>
    /// <typeparam name="T">协议帧类型</typeparam>
    public interface IResponseMatcher<T>
    {
        /// <summary>
        /// 判断响应是否匹配本次请求。
        /// </summary>
        /// <param name="response">解析得到的响应帧</param>
        /// <param name="command">发送出去的原始请求字节</param>
        /// <returns>匹配返回 true，否则 false</returns>
        bool IsResponseMatch(T response, byte[] command);

        /// <summary>
        /// 判断是否为主动上报包。
        /// 主动上报包会走上报分支，不参与请求响应匹配。
        /// </summary>
        bool IsReportPacket(T response);

        /// <summary>
        /// 处理主动上报包。
        /// 建议仅做轻量处理，避免阻塞主收发循环。
        /// </summary>
        void OnReportPacket(T response);

        /// <summary>
        /// 构建“不匹配响应”日志内容。
        /// </summary>
        string BuildUnmatchedLog(T response);
    }
}
