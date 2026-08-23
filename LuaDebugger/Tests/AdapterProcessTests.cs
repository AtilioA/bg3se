using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSE.DebuggerFrontend;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NSE.DebuggerFrontend.Tests
{
    internal static class AdapterProcessTests
    {
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
                count -= read;
            }
        }

        private static string ReadAsciiLine(Stream stream)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0)
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                }
                if (value == '\n')
                {
                    if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add((byte)value);
            }
        }

        private static JObject ReadDapSync(Stream stream)
        {
            int? contentLength = null;
            while (true)
            {
                var line = ReadAsciiLine(stream);
                if (line == null)
                {
                    return null;
                }
                if (line.Length == 0)
                {
                    break;
                }
                var separator = line.IndexOf(':');
                Require(separator > 0, "malformed DAP header");
                if (line.Substring(0, separator).Equals(
                    "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = Int32.Parse(line.Substring(separator + 1).Trim());
                }
            }

            Require(contentLength.HasValue, "DAP frame had no content length");
            var payload = new byte[contentLength.Value];
            ReadExact(stream, payload, 0, payload.Length);
            return JObject.Parse(Encoding.UTF8.GetString(payload));
        }

        private static JObject ReadDap(Process process, int timeoutMs = 5000)
        {
            var task = Task.Run(() => ReadDapSync(process.StandardOutput.BaseStream));
            if (!task.Wait(timeoutMs))
            {
                throw new TimeoutException("timed out waiting for DAP output");
            }
            return task.Result;
        }

        private static JObject ReadDapUntil(
            Process process,
            Func<JObject, bool> predicate,
            IList<JObject> observed = null)
        {
            for (var index = 0; index < 64; index++)
            {
                var message = ReadDap(process);
                Require(message != null, "adapter closed DAP output unexpectedly");
                observed?.Add(message);
                if (predicate(message))
                {
                    return message;
                }
            }
            throw new InvalidOperationException("expected DAP message was not observed");
        }

        private static void WriteDap(Process process, int sequence, string command, object arguments)
        {
            var message = new JObject
            {
                ["seq"] = sequence,
                ["type"] = "request",
                ["command"] = command,
                ["arguments"] = arguments == null ? new JObject() : JObject.FromObject(arguments)
            };
            var payload = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes(
                "Content-Length: " + payload.Length + "\r\n\r\n");
            var stream = process.StandardInput.BaseStream;
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static DebuggerToBackend ReadBackend(NetworkStream stream)
        {
            var header = new byte[4];
            ReadExact(stream, header, 0, header.Length);
            var length = BitConverter.ToInt32(header, 0);
            Require(length >= 4 && length < 1024 * 1024, "invalid backend frame length");
            var payload = new byte[length - 4];
            ReadExact(stream, payload, 0, payload.Length);
            return DebuggerToBackend.Parser.ParseFrom(payload);
        }

        private static DebuggerToBackend ReadBackendUntil(
            NetworkStream stream,
            DebuggerToBackend.MsgOneofCase messageCase)
        {
            for (var index = 0; index < 32; index++)
            {
                var message = ReadBackend(stream);
                if (message.MsgCase == messageCase)
                {
                    return message;
                }
            }
            throw new InvalidOperationException("expected backend request was not observed");
        }

        private static void WriteBackend(NetworkStream stream, BackendToDebugger message)
        {
            var payload = message.ToByteArray();
            var length = BitConverter.GetBytes(payload.Length + 4);
            stream.Write(length, 0, length.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static bool IsResponse(JObject message, int requestSequence)
        {
            return (string)message["type"] == "response"
                && (int)message["request_seq"] == requestSequence;
        }

        private static bool IsEvent(JObject message, string eventName)
        {
            return (string)message["type"] == "event"
                && (string)message["event"] == eventName;
        }

        private static TcpClient AcceptClient(TcpListener listener)
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            Require(acceptTask.Wait(5000), "adapter did not connect to fake backend");
            var client = acceptTask.Result;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            return client;
        }

        private static void ExpectConnect(NetworkStream stream, int expectedVersion, int replyVersion)
        {
            var connect = ReadBackend(stream);
            Require(connect.MsgCase == DebuggerToBackend.MsgOneofCase.Connect,
                "backend request was not connect");
            Require(connect.Connect.ProtocolVersion == expectedVersion,
                $"adapter protocol was {connect.Connect.ProtocolVersion}, expected {expectedVersion}");
            WriteBackend(stream, new BackendToDebugger
            {
                SeqNo = 1,
                ReplySeqNo = connect.SeqNo,
                ConnectResponse = new BkConnectResponse
                {
                    ProtocolVersion = (UInt32)replyVersion
                }
            });
        }

        /// <summary>
        /// Stock backend scenario: the probe receives a v4 connect response
        /// without a pair identity, so the adapter reconnects at v4 and runs
        /// in stock native mode.
        /// </summary>
        private static void Run(string adapterPath)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Process process = null;
            TcpClient backend = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = adapterPath,
                    WorkingDirectory = Path.GetDirectoryName(adapterPath),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                Require(process != null, "could not start LuaDebugger.exe");

                WriteDap(process, 1, "initialize", new
                {
                    clientID = "stock-native-test",
                    adapterID = "bg3se"
                });
                var initialize = ReadDapUntil(process, msg => IsResponse(msg, 1));
                Require((bool)initialize["success"], "initialize failed");

                WriteDap(process, 2, "launch", new
                {
                    noDebug = false,
                    backendHost = "127.0.0.1",
                    backendPort = port,
                    dbgOptions = new
                    {
                        breakOnError = false,
                        breakOnGenericError = false,
                        omitCppFrames = false
                    }
                });

                // Single stock connection at v4.
                backend = AcceptClient(listener);
                var backendStream = backend.GetStream();
                ExpectConnect(backendStream, 4, 4);
                // Stock greets with context states; mirror that.
                WriteBackend(backendStream, new BackendToDebugger
                {
                    SeqNo = 2,
                    ContextUpdated = new BkContextUpdated
                    {
                        Context = DbgContext.Server,
                        Status = BkContextUpdated.Types.Status.Loaded
                    }
                });
                WriteBackend(backendStream, new BackendToDebugger
                {
                    SeqNo = 3,
                    ContextUpdated = new BkContextUpdated
                    {
                        Context = DbgContext.Client,
                        Status = BkContextUpdated.Types.Status.Loaded
                    }
                });

                var launchResponse = ReadDapUntil(process, msg => IsResponse(msg, 2));
                Require((bool)launchResponse["success"],
                    "stock launch response was not successful");
                var bg3se = launchResponse["body"]["bg3se"];
                Require((int)bg3se["backendProtocolVersion"] == 4,
                    "launch did not report the backend protocol version");
                Require(IsEvent(ReadDapUntil(process, msg => IsEvent(msg, "initialized")),
                    "initialized"), "initialized event was missing");

                WriteDap(process, 3, "configurationDone", null);
                Require((bool)ReadDapUntil(process, msg => IsResponse(msg, 3))["success"],
                    "configurationDone failed");

                // Mirror the MCP evaluation flow: pause the server context,
                // wait for the native pause-stop, then evaluate.
                WriteDap(process, 4, "pause", new { threadId = 1 });
                var pauseReq = ReadBackendUntil(
                    backendStream, DebuggerToBackend.MsgOneofCase.Continue);
                Require(pauseReq.Continue.Action == DbgContinue.Types.Action.Pause,
                    "adapter did not request a backend pause");
                WriteBackend(backendStream, new BackendToDebugger
                {
                    SeqNo = 4,
                    ReplySeqNo = pauseReq.SeqNo,
                    Results = new BkResult { StatusCode = StatusCode.Success }
                });
                WriteBackend(backendStream, new BackendToDebugger
                {
                    SeqNo = 5,
                    BreakpointTriggered = new BkBreakpointTriggered
                    {
                        Context = DbgContext.Server,
                        Reason = BkBreakpointTriggered.Types.Reason.Pause,
                        Stack =
                        {
                            new MsgStackFrame
                            {
                                Source = "@stock",
                                Function = "main",
                                Line = 1,
                                ScopeFirstLine = 1,
                                ScopeLastLine = 1
                            }
                        }
                    }
                });
                ReadDapUntil(process, msg => IsEvent(msg, "stopped"));

                WriteDap(process, 5, "evaluate", new
                {
                    expression = "1 + 1",
                    frameId = 1 << 16,
                    context = "watch"
                });
                var eval = ReadBackendUntil(
                    backendStream, DebuggerToBackend.MsgOneofCase.Evaluate);
                Require(eval.Evaluate.Expression == "1 + 1", "eval expression was mangled");
                WriteBackend(backendStream, new BackendToDebugger
                {
                    SeqNo = 6,
                    ReplySeqNo = eval.SeqNo,
                    EvaluateResponse = new BkEvaluateResponse
                    {
                        Result = new MsgValue { TypeId = MsgValueType.String, Stringval = "2" }
                    }
                });
                var evalResponse = ReadDapUntil(process, msg => IsResponse(msg, 5));
                var evalResult = (string)evalResponse["body"]?["result"];
                Require((bool)evalResponse["success"]
                    && evalResult != null && evalResult.Contains("2"),
                    $"stock evaluate returned unexpected result: {evalResult}");

                backend.Close();
                backend = null;
                while (!process.HasExited)
                {
                    var message = ReadDap(process);
                    if (message == null)
                    {
                        break;
                    }
                    if (IsEvent(message, "terminated"))
                    {
                        break;
                    }
                }
                Require(process.WaitForExit(5000), "adapter did not exit after backend EOF");
                Require(process.ExitCode == 0, "adapter process exited with an error");
            }
            finally
            {
                listener.Stop();
                if (process != null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// Wrong-DLL scenario: a backend answering with an unexpected protocol
        /// version must fail the launch closed with protocol_mismatch.
        /// </summary>
        private static void RunProtocolMismatch(string adapterPath)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Process process = null;
            TcpClient backend = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = adapterPath,
                    WorkingDirectory = Path.GetDirectoryName(adapterPath),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                Require(process != null, "could not start LuaDebugger.exe");

                WriteDap(process, 1, "initialize", new
                {
                    clientID = "mismatch-test",
                    adapterID = "bg3se"
                });
                Require((bool)ReadDapUntil(process, msg => IsResponse(msg, 1))["success"],
                    "initialize failed");

                WriteDap(process, 2, "launch", new
                {
                    noDebug = false,
                    backendHost = "127.0.0.1",
                    backendPort = port
                });

                backend = AcceptClient(listener);
                var connect = ReadBackend(backend.GetStream());
                Require(connect.MsgCase == DebuggerToBackend.MsgOneofCase.Connect,
                    "backend request was not connect");
                WriteBackend(backend.GetStream(), new BackendToDebugger
                {
                    SeqNo = 1,
                    ReplySeqNo = connect.SeqNo,
                    ConnectResponse = new BkConnectResponse { ProtocolVersion = 3 }
                });

                var failure = ReadDapUntil(process, msg => IsResponse(msg, 2));
                Require(!(bool)failure["success"],
                    "launch against wrong backend version did not fail");
                Require((string)failure["body"]["bg3se"]["mismatchCode"] == "protocol_mismatch",
                    "launch failure did not report protocol_mismatch");
                Require(process.WaitForExit(5000),
                    "adapter did not exit after incompatible backend");
            }
            finally
            {
                listener.Stop();
                if (process != null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                    process.Dispose();
                }
            }
        }

        private static int Main(string[] args)
        {
            try
            {
                Require(args.Length == 1, "usage: AdapterProcessTests <LuaDebugger.exe>");
                Run(Path.GetFullPath(args[0]));
                RunProtocolMismatch(Path.GetFullPath(args[0]));
                Console.WriteLine("LuaDebugger process tests passed");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine("adapter stderr tail follows if present.");
                return 1;
            }
        }
    }
}






