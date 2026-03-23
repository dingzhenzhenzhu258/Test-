using SerialPortService.Models.Enums;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 解析器注册表。
    /// </summary>
    public interface IParserRegistry
    {
        ParserRegistrationResult Register<T>(ProtocolEnum protocol, string key, Func<IStreamParser<T>> factory) where T : class;

        IStreamParser<T> Create<T>(ProtocolEnum protocol) where T : class;
    }
}
