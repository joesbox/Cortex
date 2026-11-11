using Cortex.Models;
using Cortex.Services;
using System.Reflection;
using System.Text;
using Xunit;

namespace Cortex.Tests;

public class PlaintextLogBulkTests
{
    [Theory]
    [InlineData(0x03, false)]
    [InlineData(0x0B, true)]
    public void Info_AdvertisesOptionalPlaintextLogs(byte flags, bool supported)
    {
        byte[] response = new byte[47];
        response[0] = (byte)'a';
        response[3] = 42;
        response[5] = 1;
        response[6] = flags;
        object?[] args = [response, null];
        MethodInfo method = typeof(SerialPortService).GetMethod("TryParseSecureInfoResponse",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.True((bool)method.Invoke(null, args)!);
        Assert.Equal(supported, Assert.IsType<SecureInfoResponse>(args[1]).SupportsPlaintextLogBulk);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void PlaintextLogs_RequireOpenAuthorizedSupportingSession(bool open, bool authorized, bool supported, bool expected)
    {
        using var service = new SerialPortService("COM1");
        object session = Session(service);
        Set(session, "IsSessionOpen", open);
        Set(session, "IsAuthorized", authorized);
        Set(session, "SupportsPlaintextLogBulk", supported);
        Assert.Equal(expected, service.CanUsePlaintextLogBulk);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkReader_HandlesFragmentedPacketsAndEmbeddedFramePrefixes(bool secure)
    {
        using var service = new SerialPortService("COM1");
        using var stream = new FragmentedStream([
            .. Packet("Date,!BL\n", false),
            .. Packet("2026,123\n", true),
        ]);
        var progress = new List<byte>();
        string text = await Read(service, stream, secure, value => progress.Add(value));
        Assert.Equal("Date,!BL\n2026,123\n", text);
        Assert.Equal(new byte[] { 50, 100 }, progress);
        Assert.Equal((uint)0, Get<uint>(Session(service), "RxSequence"));
        Assert.Empty(stream.Written.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkReader_PreservesSecureSequencesAndDrainsKeepaliveAfterFinalPacket(bool statusAfterFinal)
    {
        using var service = new SerialPortService("COM1");
        object session = Session(service);
        Set(session, "IsSessionOpen", true);
        Set(session, "IsAuthorized", true);
        byte[] status = EncryptedStatus(authorized: true);
        using var stream = new FragmentedStream(statusAfterFinal
            ? [.. Packet("first", false), .. Packet("last", true), .. status]
            : [.. Packet("first", false), .. status, .. Packet("last", true)]);
        string text = await Read(service, stream, true, progress =>
        {
            if (progress == 50) Thread.Sleep(1100);
        });

        Assert.Equal("firstlast", text);
        Assert.Equal((uint)1, Get<uint>(session, "RxSequence"));
        Assert.Equal((uint)1, Get<uint>(session, "TxSequence"));
        Assert.Equal((byte)'!', stream.Written.ToArray()[0]);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(service.IsSecureSessionAuthorized);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("truncated")]
    [InlineData("length")]
    [InlineData("header")]
    public async Task BulkReader_RejectsMalformedPackets(string damage)
    {
        using var service = new SerialPortService("COM1");
        byte[] packet = Packet("text", true);
        switch (damage)
        {
            case "checksum": packet[^1] ^= 1; break;
            case "truncated": packet = packet[..^1]; break;
            case "length": packet[5] = 0x7F; break;
            case "header": packet[0] = (byte)'!'; break;
        }
        using var stream = new FragmentedStream(packet);
        await Assert.ThrowsAsync<IOException>(() => Read(service, stream, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkReader_RejectsTamperedOrUnauthorizedKeepalive(bool tamper)
    {
        using var service = new SerialPortService("COM1");
        object session = Session(service);
        Set(session, "IsSessionOpen", true);
        Set(session, "IsAuthorized", true);
        byte[] status = EncryptedStatus(authorized: false);
        if (tamper) status[^1] ^= 1;
        using var stream = new FragmentedStream([.. Packet("first", false), .. status]);
        await Assert.ThrowsAsync<IOException>(() => Read(service, stream, true, _ => Thread.Sleep(1100)));
        if (tamper) Assert.False(service.IsSecureSessionAuthorized);
    }

    [Fact]
    public async Task BulkReader_ObservesCancellation()
    {
        using var service = new SerialPortService("COM1");
        using var stream = new FragmentedStream([.. Packet("text", true), .. Encrypt([(byte)'c'])]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(service, stream, true, token: cancellation.Token));
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal((uint)1, Get<uint>(Session(service), "RxSequence"));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 8)]
    [InlineData(true, 16)]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    [InlineData(false, 16)]
    public async Task BulkReader_CancelsInsidePacketDrainsQueuedDataAndCanDownloadAgain(bool secure, int cancelAt)
    {
        using var service = new SerialPortService("COM1");
        object session = Session(service);
        Set(session, "IsSessionOpen", secure);
        Set(session, "IsAuthorized", true);
        using var cancellation = new CancellationTokenSource();
        byte[] ack = secure ? Encrypt([(byte)'c']) : [(byte)'c'];
        using var stream = new FragmentedStream([
            .. Packet("inflight", false), .. Packet("queued", true), .. ack,
        ])
        {
            AfterRead = position =>
            {
                if (position >= cancelAt) cancellation.Cancel();
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Read(service, stream, secure, token: cancellation.Token));
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(secure, service.IsSecureSessionAuthorized);
        Assert.True((bool)typeof(SerialPortService).GetField("_bulkLogCancellationDrained",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!);
        Assert.Single(secure ? DecryptRequests(stream.Written.ToArray()) : stream.Written.ToArray().Select(value => new byte[] { value }));
        Assert.Equal(new byte[] { (byte)'x' }, secure
            ? DecryptRequests(stream.Written.ToArray()).Single()
            : stream.Written.ToArray());

        using var next = new FragmentedStream(Packet("different file", true));
        Assert.Equal("different file", await Read(service, next, secure));
        Assert.Equal(secure ? (uint)1 : (uint)0, Get<uint>(session, "RxSequence"));
    }

    [Fact]
    public async Task BulkReader_CancelButtonAndPendingStatusSendOneCancelAndPreserveSession()
    {
        using var service = new SerialPortService("COM1");
        Set(Session(service), "IsSessionOpen", true);
        Set(Session(service), "IsAuthorized", true);
        typeof(SerialPortService).GetField("_bulkLogTransferActive", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, true);
        byte[] status = EncryptedStatus(true);
        using var stream = new FragmentedStream([
            .. Packet("first", false), .. status, .. Packet("queued", false), .. Encrypt([(byte)'c'], 1),
        ])
        {
            AfterRead = position =>
            {
                if (position > Packet("first", false).Length)
                {
                    service.CancelLogTransfer();
                    service.CancelLogTransfer();
                }
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Read(service, stream, true, _ => Thread.Sleep(1100)));
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(new byte[][] { [(byte)'a', 9], [(byte)'x'] }, DecryptRequests(stream.Written.ToArray()));
        Assert.True(service.IsSecureSessionAuthorized);
        Assert.Equal((uint)2, Get<uint>(Session(service), "RxSequence"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkReader_CancelRequiresAuthenticPositiveAcknowledgement(bool tamper)
    {
        using var service = new SerialPortService("COM1");
        using var cancellation = new CancellationTokenSource();
        byte[] ack = Encrypt([tamper ? (byte)'c' : (byte)'f']);
        if (tamper) ack[^1] ^= 1;
        using var stream = new FragmentedStream([.. Packet("first", false), .. ack]);
        await Assert.ThrowsAsync<IOException>(() =>
            Read(service, stream, true, _ => cancellation.Cancel(), cancellation.Token));
    }

    private static List<byte[]> DecryptRequests(byte[] data)
    {
        var result = new List<byte[]>();
        for (int offset = 0; offset < data.Length;)
        {
            int length = data[offset + 1] | data[offset + 2] << 8;
            using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(new byte[32]);
            byte[] nonce = new byte[12];
            data.AsSpan(offset + 3, 4).CopyTo(nonce.AsSpan(8));
            byte[] plaintext = new byte[length];
            cipher.Decrypt(nonce, data.AsSpan(offset + 7, length), data.AsSpan(offset + 7 + length, 16),
                plaintext, data.AsSpan(offset, 7));
            result.Add(plaintext);
            offset += length + 23;
        }
        return result;
    }

    [Fact]
    public async Task BulkReader_CancellationOnFinalPacketDrainsPendingStatusAndCancelAck()
    {
        using var service = new SerialPortService("COM1");
        Set(Session(service), "IsSessionOpen", true);
        Set(Session(service), "IsAuthorized", true);
        using var cancellation = new CancellationTokenSource();
        using var stream = new FragmentedStream([
            .. Packet("first", false), .. Packet("last", true),
            .. EncryptedStatus(true), .. Encrypt([(byte)'c'], 1),
        ]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Read(service, stream, true, progress =>
            {
                if (progress == 50) Thread.Sleep(1100);
                else cancellation.Cancel();
            }, cancellation.Token));
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(service.IsSecureSessionAuthorized);
        Assert.Equal((uint)2, Get<uint>(Session(service), "RxSequence"));
        Assert.Equal(new byte[][] { [(byte)'a', 9], [(byte)'x'] }, DecryptRequests(stream.Written.ToArray()));
    }

    [Fact]
    public async Task ExactReadCancelsEvenWhenStreamIgnoresCancellation()
    {
        using var stream = new NonCancellingStream();
        using var cancellation = new CancellationTokenSource();
        byte[] buffer = new byte[1];
        var read = (Task)typeof(SerialPortService).GetMethod("ReadExactlyAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [stream, buffer, 0, 1, cancellation.Token])!;
        Assert.False(read.IsCompleted);

        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            stream.CompleteRead();
        }
    }

    private sealed class NonCancellingStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_read.Task);

        public void CompleteRead() => _read.TrySetResult(0);
    }

    [Fact]
    public async Task BulkReader_CancellationCannotSucceedWithoutAcknowledgement()
    {
        using var service = new SerialPortService("COM1");
        using var cancellation = new CancellationTokenSource();
        using var stream = new FragmentedStream(Packet("inflight", false));
        await Assert.ThrowsAsync<IOException>(() =>
            Read(service, stream, true, _ => cancellation.Cancel(), cancellation.Token));
        Assert.False((bool)typeof(SerialPortService).GetField("_bulkLogCancellationDrained",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!);
    }

    private static Task<string> Read(SerialPortService service, Stream stream, bool secure,
        Action<byte>? progress = null, CancellationToken token = default)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("ReadLogBulkPacketsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<string>)method.Invoke(service, [stream, secure, progress, token])!;
    }

    private static byte[] Packet(string text, bool done)
    {
        byte[] payload = Encoding.ASCII.GetBytes(text);
        var packet = new List<byte> { (byte)'B', (byte)'L', done ? (byte)100 : (byte)50,
            done ? (byte)1 : (byte)0, (byte)payload.Length, (byte)(payload.Length >> 8) };
        packet.AddRange(payload);
        uint checksum = 0;
        foreach (byte value in packet) checksum += value;
        packet.AddRange(BitConverter.GetBytes(checksum));
        return packet.ToArray();
    }

    private static byte[] EncryptedStatus(bool authorized)
    {
        byte[] plaintext = [(byte)'a', 9, 0, 0, 0, 0, 0, authorized ? (byte)3 : (byte)1];
        return Encrypt(plaintext);
    }

    private static byte[] Encrypt(byte[] plaintext, uint sequence = 0)
    {
        MethodInfo method = typeof(SerialPortService).GetMethod("EncryptSecureEnvelope",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (byte[])method.Invoke(null, [plaintext, new byte[32], new byte[8], sequence])!;
    }

    private static object Session(SerialPortService service) =>
        typeof(SerialPortService).GetField("_secureSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static void Set(object session, string name, object value) =>
        session.GetType().GetProperty(name)!.SetValue(session, value);

    private static T Get<T>(object session, string name) =>
        (T)session.GetType().GetProperty(name)!.GetValue(session)!;

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public MemoryStream Written { get; } = new();
        public Action<long>? AfterRead { get; init; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
            AfterRead?.Invoke(Position);
            return read;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Written.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) Written.Dispose();
            base.Dispose(disposing);
        }
    }
}
