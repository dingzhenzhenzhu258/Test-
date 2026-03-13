using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Xunit;

namespace InfraExtensions.Tests;

public class RuntimeExceptionMetricSmokeTests
{
    [Fact]
    public void RuntimeInstrumentation_WhenThrowingExceptions_WithServiceName_ShouldFlushSuccessfully()
    {
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("infraextensions-tests"))
            .AddRuntimeInstrumentation()
            .Build();

        for (var i = 0; i < 3; i++)
        {
            try
            {
                throw new InvalidOperationException("smoke-test-exception-service-name");
            }
            catch
            {
            }
        }

        var flushed = meterProvider.ForceFlush();
        Assert.True(flushed);
    }

    [Fact]
    public void RuntimeInstrumentation_WhenThrowingExceptions_ShouldExportExceptionCounter()
    {
        var exporter = new ExceptionMetricExporter();

        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("infraextensions-tests"))
            .AddRuntimeInstrumentation()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 100, exportTimeoutMilliseconds: 1000))
            .Build();

        for (var i = 0; i < 3; i++)
        {
            try
            {
                throw new InvalidOperationException("smoke-test-exception");
            }
            catch
            {
            }
        }

        meterProvider.ForceFlush();

        Assert.True(exporter.SeenExceptionMetric, "未捕获到 process.runtime.dotnet.exceptions.count 指标导出。");
        Assert.True(exporter.LastExportedExceptionCount > 0, "异常计数指标存在，但导出值为 0。");
    }

    private sealed class ExceptionMetricExporter : BaseExporter<Metric>
    {
        public bool SeenExceptionMetric { get; private set; }
        public long LastExportedExceptionCount { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (!string.Equals(metric.Name, "process.runtime.dotnet.exceptions.count", StringComparison.Ordinal))
                {
                    continue;
                }

                SeenExceptionMetric = true;

                foreach (var metricPoint in metric.GetMetricPoints())
                {
                    LastExportedExceptionCount += metricPoint.GetSumLong();
                }
            }

            return ExportResult.Success;
        }
    }
}
