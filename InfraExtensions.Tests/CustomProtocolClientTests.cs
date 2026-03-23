using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SerialPortService.Models;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Custom.Commands;
using Xunit;

namespace InfraExtensions.Tests;

public class CustomProtocolClientTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldForwardCommandAndDecodeResponse()
    {
        var context = new Mock<ICustomProtocolContext>(MockBehavior.Strict);
        byte[]? captured = null;
        context.Setup(x => x.ReadParsedPacketsAsync(It.IsAny<CancellationToken>()))
            .Returns(EmptyFrames());
        context.As<IRequestResponseContext<CustomFrame>>()
            .Setup(x => x.SendRequestAsync(It.IsAny<byte[]>(), 1200, 2, It.IsAny<CancellationToken>()))
            .Callback<byte[], int, int, CancellationToken>((request, _, _, _) => captured = request)
            .ReturnsAsync(new CustomFrame(0x33, new byte[] { 0xAA }, new byte[] { 0xAA, 0x03, 0x33, 0xAA, 0x9A, 0x55 }));

        var sut = new CustomProtocolClient(context.Object, NullLogger.Instance);
        var command = new TestCustomCommand(0x33, new byte[] { 0x01, 0x02 });

        var result = await sut.ExecuteAsync(command, 1200, 2);

        Assert.Equal(0xAA, result);
        Assert.Equal(new byte[] { 0xAA, 0x03, 0x33, 0x01, 0x02, 0x33, 0x55 }, captured);
    }

    private sealed class TestCustomCommand : CustomProtocolCommandBase<byte>
    {
        public TestCustomCommand(byte command, byte[] payload) : base(command, payload)
        {
        }

        public override byte DecodeResponse(CustomFrame response) => response.Payload[0];
    }

    private static async IAsyncEnumerable<CustomFrame> EmptyFrames()
    {
        await Task.CompletedTask;
        yield break;
    }
}
