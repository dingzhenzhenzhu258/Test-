using Microsoft.Extensions.DependencyInjection;
using SerialPortService.Services;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Extensions
{
    public static class SerialPortServiceExtensions
    {
        public static IServiceCollection AddSerialPortService(this IServiceCollection services)
        {
            services.AddSingleton<ISerialPortService, SerialPortServiceBase>();
            return services;
        }
    }
}
