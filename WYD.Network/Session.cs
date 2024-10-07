using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace WYD.Network;

public sealed class Session : ISession
{
    private readonly Socket _socket;
    private readonly IProtocol _protocol;
    private readonly ILogger<Session> _logger;

    private readonly PipeWriter _pipeWriter;
    private readonly PipeReader _pipeReader;

    public Session(Socket socket, IProtocol protocol, ILoggerFactory loggerFactory)
    {
        _socket = socket;
        _protocol = protocol;
        _logger = loggerFactory.CreateLogger<Session>();

        _pipeWriter = PipeWriter.Create(new NetworkStream(_socket));
        _pipeReader = PipeReader.Create(new NetworkStream(_socket));
    }

    public async Task RunAsync()
    {
        try
        {
            if (!await ParseHandshakeAsync())
            {
                _logger.LogInformation("Invalid handshake received. Closing session");
                return;
            }

            await _protocol.OnConnectedAsync(this);

            await HandleIncomingPacketsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred");
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Error shutting down socket");
        }

        _socket.Close();

        await _protocol.OnDisconnectedAsync(this);
    }

    private async Task HandleIncomingPacketsAsync()
    {
        while (true)
        {
            var packetBuffer = await ReadPacketAsync();
            var payload = new ReadOnlyMemory<byte>(packetBuffer);
            // TODO: Decrypt packet

            // TODO: process packet

            ArrayPool<byte>.Shared.Return(packetBuffer);
        }
    }

    private async Task<bool> ParseHandshakeAsync()
    {
        const uint handshakeCode = 0x1F11F311;
        var receivedHandshakeToken = await ReceiveHandshakeAsync();
        return receivedHandshakeToken == handshakeCode;
    }

    private async Task<uint> ReceiveHandshakeAsync()
    {
        var result = await _pipeReader.ReadAsync();
        var buffer = result.Buffer;
        const int handshakeSize = 4;

        // Loop until we have the handshake size
        while (buffer.Length < handshakeSize)
        {
            _pipeReader.AdvanceTo(buffer.Start, buffer.End);

            // Not enough data, read more
            result = await _pipeReader.ReadAsync();
            MaybeThrowEndOfStream(result, buffer);
            buffer = result.Buffer;
        }

        var handshakeToken = ReadUInt32(buffer);
        // Advance the buffer
        buffer = buffer.Slice(handshakeSize);

        _pipeReader.AdvanceTo(buffer.Start);
        return handshakeToken;
    }

    private async Task<byte[]> ReadPacketAsync()
    {
        var result = await _pipeReader.ReadAsync();
        var buffer = result.Buffer;

        MaybeThrowEndOfStream(result, buffer);

        byte[] readBuffer = [];
        while (!TryParsePacket(ref buffer, ref readBuffer))
        {
            _pipeReader.AdvanceTo(buffer.Start, buffer.End);

            // Not enough data, read more
            result = await _pipeReader.ReadAsync();
            MaybeThrowEndOfStream(result, buffer);
            buffer = result.Buffer;
        }

        _pipeReader.AdvanceTo(buffer.Start);
        return readBuffer;
    }

    // This should be wrapped on a different class, where it will manage the rented buffer to avoid mistakes
    private static bool TryParsePacket(ref ReadOnlySequence<byte> buffer, ref byte[] readBuffer)
    {
        const int packetSizeLength = 2;
        // At least the packet size
        if (buffer.Length < packetSizeLength)
        {
            return false;
        }

        var packetSize = ReadUInt16(buffer);
        if (buffer.Length < packetSize)
        {
            return false;
        }

        var sizeToRead = packetSize - packetSizeLength;
        readBuffer = ArrayPool<byte>.Shared.Rent(sizeToRead);

        var framePayload = buffer.Slice(packetSizeLength, sizeToRead);
        framePayload.CopyTo(readBuffer);

        // Advance the buffer
        buffer = buffer.Slice(packetSize);
        return true;
    }

    // This should be on a utility class 
    private static ushort ReadUInt16(ReadOnlySequence<byte> buffer)
    {
        // First, we try to get the value from the first span
        // This is correct, and it's part of the guidance on how to use span
        if (BinaryPrimitives.TryReadUInt16LittleEndian(buffer.First.Span, out var value))
            return value;

        // If we couldn't get from the first span only, we need to make a slice from the sequence
        Span<byte> bytes = stackalloc byte[2];
        buffer.Slice(0, 2).CopyTo(bytes);
        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return value;
    }

    private static uint ReadUInt32(ReadOnlySequence<byte> buffer)
    {
        // First, we try to get the value from the first span
        if (BinaryPrimitives.TryReadUInt32LittleEndian(buffer.First.Span, out var value))
            return value;

        // If we couldn't get from the first span only, we need to make a slice from the sequence
        Span<byte> bytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(bytes);
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value;
    }

    private static void MaybeThrowEndOfStream(ReadResult result, ReadOnlySequence<byte> buffer)
    {
        if (result.IsCompleted && buffer.IsEmpty)
        {
            throw new EndOfStreamException("Pipe is completed");
        }
    }
}