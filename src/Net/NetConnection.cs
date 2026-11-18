// One TCP connection of a networked game (docs/MULTIPLAYER.md "Transport"): messages are JSON objects, each sent as a
// frame - 4 bytes of length (little endian) and the UTF-8 text. A reader thread turns the incoming frames into
// messages in a queue; the game's thread takes them out (Poll / Update), so everything that changes a lobby or a game
// happens on one thread. Sending is thread-safe and never waits for the other side beyond the socket's buffer.
//
// Why TCP and not UDP / ENet: lockstep needs every command of every player, in order, on every machine - exactly what
// TCP delivers; a lost packet costs a retransmission (a few tens of ms, hidden by the input delay of the turns), and a
// lockstep game has to wait for the missing turn anyway. System.Net.Sockets is part of .NET (no native library, works
// the same on Windows and Linux and in a dedicated server without Godot); Godot's ENet would tie the network layer to
// the engine. Nagle's algorithm is off (NoDelay): the turns are small and due at once.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PwRemake.Sim.Core;

namespace PwRemake.Net;

public sealed class NetConnection : IDisposable
{
    static readonly Logger L = Log.For("Net");
    /// <summary>the largest frame accepted (a desync dump of a big mission is a few MB)</summary>
    public const int MaxFrame = 64 << 20;
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly object _sendLock = new();
    readonly Thread _reader;
    /// <summary>the messages received and not yet taken (the game's thread takes them)</summary>
    public readonly ConcurrentQueue<JsonObject> Inbox = new();
    volatile bool _closed;
    public bool Closed => _closed;
    /// <summary>why it closed (null while open)</summary>
    public string? Reason { get; private set; }
    /// <summary>the other side, for the log and the lobby</summary>
    public readonly string Remote;
    public long BytesIn, BytesOut, FramesIn, FramesOut;

    public NetConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "net-read " + Remote };
        _reader.Start();
    }

    /// <summary>connects to a host (blocking, with a timeout); throws with a readable message</summary>
    public static NetConnection Connect(string host, int port, int timeoutMs = 5000)
    {
        var tcp = new TcpClient();
        try
        {
            var task = tcp.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs)) throw new IOException($"no answer from {host}:{port}");
        }
        catch (AggregateException e) when (e.InnerException is SocketException se) { tcp.Dispose(); throw new IOException($"could not connect to {host}:{port}: {se.Message}"); }
        catch (Exception) { tcp.Dispose(); throw; }
        return new NetConnection(tcp);
    }

    public void Send(JsonObject msg)
    {
        if (_closed) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var head = BitConverter.GetBytes(bytes.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(head);
        try
        {
            lock (_sendLock)
            {
                _stream.Write(head, 0, 4);
                _stream.Write(bytes, 0, bytes.Length);
                BytesOut += 4 + bytes.Length; FramesOut++;
            }
        }
        catch (Exception e) { Close("send failed: " + e.Message); }
    }

    void ReadLoop()
    {
        var head = new byte[4];
        try
        {
            while (!_closed)
            {
                if (!ReadExactly(head, 4)) { Close("the other side closed the connection"); return; }
                if (!BitConverter.IsLittleEndian) Array.Reverse(head);
                int n = BitConverter.ToInt32(head, 0);
                if (n < 0 || n > MaxFrame) { Close($"bad frame length {n}"); return; }
                var buf = new byte[n];
                if (!ReadExactly(buf, n)) { Close("the connection ended inside a message"); return; }
                BytesIn += 4 + n; FramesIn++;
                JsonObject? m = null;
                try { m = JsonNode.Parse(buf) as JsonObject; } catch (Exception e) { L.Warn($"{Remote}: a message that is no JSON object ({e.Message})"); }
                if (m != null) Inbox.Enqueue(m);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { Close("connection lost: " + e.Message); }
    }

    bool ReadExactly(byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = _stream.Read(buf, got, n - got);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    public void Close(string why)
    {
        if (_closed) return;
        Reason = why;
        _closed = true;
        L.Info($"connection {Remote} closed: {why}");
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        try { _tcp.Close(); } catch (Exception) { }
    }

    public void Dispose() => Close("closed");
}
