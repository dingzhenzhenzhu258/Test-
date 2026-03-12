using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Logger.Extensions
{
    /// <summary>
    /// 用于获取当前上位机/服务器的物理环境与版本信息。
    /// 这些信息会作为全局静态属性注入日志，便于跨设备问题定位。
    /// </summary>
    public static class GlobalDeviceInfo
    {
        /// <summary>
        /// 当前机器名。
        /// </summary>
        public static string MachineName => Environment.MachineName;

        /// <summary>
        /// 启动程序集版本号。
        /// </summary>
        public static string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown";

        /// <summary>
        /// 本机首个可用 IPv4 地址。
        /// </summary>
        public static string IpAddress => GetLocalIPv4();

        /// <summary>
        /// 本机首个可用网卡 MAC 地址。
        /// </summary>
        public static string MacAddress => GetMacAddress();

        /// <summary>
        /// 获取本机首个可用 IPv4 地址。
        /// </summary>
        private static string GetLocalIPv4()
        {
            try
            {
                foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 找一个正在运行的、非回环的本地网络接口
                    if (netInterface.OperationalStatus == OperationalStatus.Up &&
                        netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var properties = netInterface.GetIPProperties();
                        var ipv4 = properties.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        if (ipv4 != null) return ipv4.Address.ToString();
                    }
                }
            }
            catch { }
            return "Unknown IP";
        }

        /// <summary>
        /// 获取本机首个可用网卡的 MAC 地址。
        /// </summary>
        private static string GetMacAddress()
        {
            try
            {
                foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netInterface.OperationalStatus == OperationalStatus.Up &&
                        netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var mac = netInterface.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac))
                        {
                            // 将连续的 MAC 地址格式化为 XX-XX-XX-XX-XX-XX 便于阅读
                            return string.Join("-", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2)));
                        }
                    }
                }
            }
            catch { }
            return "Unknown MAC";
        }
    }
}
