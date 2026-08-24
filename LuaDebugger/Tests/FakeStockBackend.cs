using Google.Protobuf;
using NSE.DebuggerFrontend;
using System;
using System.Net;
using System.Net.Sockets;

internal static class FakeStockBackend
{
    private static void Main(string[] args)
    {
        int port = int.Parse(args[0]);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine("backend-ready " + port);
        while (true)
        {
            var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var buffer = new byte[65536];
                    int pos = 0;
                    uint inbound = 1;
                    while (true)
                    {
                        int n = stream.Read(buffer, pos, buffer.Length - pos);
                        if (n == 0) { client.Close(); return; }
                        pos += n;
                        while (pos >= 4)
                        {
                            uint len = BitConverter.ToUInt32(buffer, 0);
                            if (pos < len) break;
                            var msg = DebuggerToBackend.Parser.ParseFrom(buffer, 4, (int)len - 4);
                            pos -= (int)len;
                            Array.Copy(buffer, len, buffer, 0, pos);
                            if (msg.MsgCase == DebuggerToBackend.MsgOneofCase.Connect)
                            {
                                var resp = new BackendToDebugger
                                {
                                    SeqNo = inbound++,
                                    ReplySeqNo = msg.SeqNo,
                                    ConnectResponse = new BkConnectResponse { ProtocolVersion = 4 }
                                };
                                Write(stream, resp);
                                foreach (var ctx in new[] { DbgContext.Server, DbgContext.Client })
                                {
                                    Write(stream, new BackendToDebugger
                                    {
                                        SeqNo = inbound++,
                                        ContextUpdated = new BkContextUpdated
                                        {
                                            Context = ctx,
                                            Status = BkContextUpdated.Types.Status.Loaded
                                        }
                                    });
                                }
                                Write(stream, new BackendToDebugger { SeqNo = inbound++, DebuggerReady = new BkDebuggerReady() });
                            }
                        }
                    }
                }
                catch (Exception e) { Console.Error.WriteLine("conn err: " + e.Message); }
            });
        }
    }

    private static void Write(NetworkStream s, BackendToDebugger msg)
    {
        var payload = msg.ToByteArray();
        var head = BitConverter.GetBytes(payload.Length + 4);
        s.Write(head, 0, 4); s.Write(payload, 0, payload.Length); s.Flush();
    }
}
