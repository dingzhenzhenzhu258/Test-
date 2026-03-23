namespace SerialPortService.Services.Protocols
{
    /// <summary>
    /// 协议命令定义。
    /// 将构帧、响应校验和响应解码聚合为可复用对象。
    /// </summary>
    /// <typeparam name="TPacket">协议响应包类型</typeparam>
    /// <typeparam name="TResult">业务结果类型</typeparam>
    public interface IProtocolCommand<TPacket, TResult>
    {
        byte[] BuildRequest();

        void ValidateResponse(TPacket response);

        TResult DecodeResponse(TPacket response);
    }
}
