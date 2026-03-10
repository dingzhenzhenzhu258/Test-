using Moq;
using SerialPortService.Models;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using Xunit;

namespace InfraExtensions.Tests;

public class ModbusRtuClientTests
{
    [Fact]
    public async Task ReadHoldingRegistersAsync_WhenCountOutOfRange_ShouldThrow()
    {
        var ctx = CreateContextMock();
        var sut = new ModbusRtuClient(ctx.Object, 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.ReadHoldingRegistersAsync(0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.ReadHoldingRegistersAsync(0, 126));
    }

    [Fact]
    public async Task WriteMultipleRegistersAsync_WhenTooManyRegisters_ShouldThrow()
    {
        var ctx = CreateContextMock();
        var sut = new ModbusRtuClient(ctx.Object, 1);

        var values = Enumerable.Range(0, 124).Select(i => (ushort)i).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.WriteMultipleRegistersAsync(0, values));
    }

    [Fact]
    public async Task ReadHoldingRegistersAsync_WhenErrorResponse_ShouldThrowModbusException()
    {
        var ctx = CreateContextMock();
        ctx.Setup(x => x.SendRequestAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModbusPacket
            {
                FunctionCode = 0x83,
                Data = new byte[] { 0x02 },
                RawFrame = new byte[] { 0x01, 0x83, 0x02 }
            });

        var sut = new ModbusRtuClient(ctx.Object, 1);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => sut.ReadHoldingRegistersAsync(0, 1));
        Assert.Equal((byte)0x02, ex.ErrorCode);
    }

    [Fact]
    public async Task WriteSingleRegisterAsync_WhenUnexpectedFunctionCode_ShouldThrowProtocolMismatchException()
    {
        var ctx = CreateContextMock();
        ctx.Setup(x => x.SendRequestAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModbusPacket
            {
                FunctionCode = 0x03,
                Data = Array.Empty<byte>(),
                RawFrame = new byte[] { 0x01, 0x03 }
            });

        var sut = new ModbusRtuClient(ctx.Object, 1);

        await Assert.ThrowsAsync<ProtocolMismatchException>(() => sut.WriteSingleRegisterAsync(0, 1));
    }

    [Fact]
    public async Task BatchWriteAsync_WhenContinuousDataExceeds123_ShouldSplitIntoValidChunks()
    {
        var ctx = CreateContextMock();
        var sentCommands = new List<byte[]>();

        ctx.Setup(x => x.SendRequestAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], int, int, CancellationToken>((cmd, _, _, _) => sentCommands.Add(cmd))
            .ReturnsAsync((byte[] cmd, int _, int _, CancellationToken _) =>
            {
                var function = cmd[1];
                return new ModbusPacket
                {
                    FunctionCode = function,
                    Data = Array.Empty<byte>(),
                    RawFrame = cmd
                };
            });

        var sut = new ModbusRtuClient(ctx.Object, 1);
        var writes = Enumerable.Range(0, 200).Select(i => ((ushort)i, (ushort)(i + 1000))).ToArray();

        await sut.BatchWriteAsync(writes);

        Assert.Equal(2, sentCommands.Count);
        Assert.All(sentCommands, cmd =>
        {
            Assert.Equal((byte)0x10, cmd[1]);
            var regCount = (cmd[4] << 8) | cmd[5];
            Assert.InRange(regCount, 1, 123);
        });
    }

    [Fact]
    public async Task ReadHoldingRegistersAsync_ShouldForwardCancellationToken()
    {
        var ctx = CreateContextMock();
        var cts = new CancellationTokenSource();

        ctx.Setup(x => x.SendRequestAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModbusPacket
            {
                FunctionCode = 0x03,
                Data = new byte[] { 0x02, 0x12, 0x34 },
                RawFrame = new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 }
            });

        var sut = new ModbusRtuClient(ctx.Object, 1);

        await sut.ReadHoldingRegistersAsync(0, 1, cts.Token);

        ctx.Verify(x => x.SendRequestAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.Is<CancellationToken>(t => t == cts.Token)), Times.Once);
    }

    private static Mock<IModbusContext> CreateContextMock()
    {
        var mock = new Mock<IModbusContext>(MockBehavior.Strict);
        mock.Setup(x => x.ReadParsedPacketsAsync(It.IsAny<CancellationToken>()))
            .Returns(EmptyPackets());
        return mock;
    }

    private static async IAsyncEnumerable<ModbusPacket> EmptyPackets()
    {
        await Task.CompletedTask;
        yield break;
    }
}
