using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NSE.DebuggerFrontend
{
    public class StackFrame
    {
        // Backend function name
        public string Function;
        // Backend source alias (eg. file name or C++ code)
        public string Source;
        // Source path (null if no source or internal file)
        public string Path;
        public int Line;
        // First line of function/scope
        public int ScopeFirstLine;
        // Last line of function/scope
        public int ScopeLastLine;
    }

    public class ThreadState
    {
        public DbgContext Context;
        // Is the thread/context active on the backend?
        // (i.e. is there a running server/client in the game)
        public bool Active = false;

        public List<StackFrame> Stack;

        public bool Stopped;

        public void Reset()
        {
            Active = false;
            Stack = null;
            Stopped = false;
        }
    }

    public class ModuleInfo
    {
        public string Guid;
        public string Name;
        public string Author;
        public string Path;
    }

    public class BreakpointInfo
    {
        public int Id;
        public string Path;
        public int Line;
    }


    public class SourceInfo
    {
        public string Name;
        public string Path;
    }


    public class DAPMessageHandler
    {
        private class PendingControl
        {
            public DAPRequest Request;
            public DbgContext Context;
            public DbgContinue.Types.Action Action;
        }

        // DBG protocol version (game/editor backend to debugger frontend communication)
        private const UInt32 DBGProtocolVersion = 4;

        // DAP thread ID for Lua server context
        private const int ServerThreadId = 1;

        // DAP thread ID for Lua client context
        private const int ClientThreadId = 2;

        public DAPStream Stream;
        private Stream LogStream;

        private Thread DbgThread;
        private AsyncProtobufClient DbgClient;
        public DebuggerClient DbgCli;
        private DAPCustomConfiguration Config;
        private ThreadState ServerState;
        private ThreadState ClientState;
        private List<ModuleInfo> Modules;
        private List<SourceInfo> SourceFiles;
        // List of DAP source code fetch requests that are currently pending
        // (i.e. we're waiting for a debugger backend response).
        // Backend sequence ID => original DAP request map
        private Dictionary<uint, DAPRequest> PendingSourceRequests = new Dictionary<uint, DAPRequest>();

        private Dictionary<string, Dictionary<int, BreakpointInfo>> Breakpoints = new Dictionary<string, Dictionary<int, BreakpointInfo>>();
        private int NextBreakpointId = 1;

        private ExpressionEvaluator Evaluator;
        private DAPRequest PendingLaunchRequest;
        private bool PairCompatible;
        private string BackendHost;
        private int BackendPort;
        private BkConnectResponse BackendHandshake;
        private bool InitializedSent;
        private readonly object HandshakeLock = new object();
        private DAPRequest PendingLoadedSourcesRequest;
        private readonly BlockingCollection<Action> StateQueue = new BlockingCollection<Action>();
        private readonly Thread StateOwnerThread;
        private volatile int StateOwnerThreadId;
        private volatile bool SessionClosed;
        private bool TerminationSent;
        private readonly HashSet<DbgContext> InitialContexts = new HashSet<DbgContext>();
        private readonly Dictionary<DbgContext, BkContextUpdated.Types.Status> ContextStates = new Dictionary<DbgContext, BkContextUpdated.Types.Status>();
        private readonly Dictionary<uint, PendingControl> PendingControls = new Dictionary<uint, PendingControl>();
        private bool ContextsReady;


        public DAPMessageHandler(DAPStream stream)
        {
            Stream = stream;
            Stream.MessageReceived += this.MessageReceived;
            Stream.InputClosed += this.InputClosed;
            ServerState = new ThreadState();
            ServerState.Context = DbgContext.Server;
            ClientState = new ThreadState();
            ClientState.Context = DbgContext.Client;
            Evaluator = new ExpressionEvaluator(this);
            StateOwnerThread = new Thread(StateOwnerMain)
            {
                IsBackground = true,
                Name = "DAP state owner"
            };
            StateOwnerThread.Start();
        }

        public void EnableLogging(Stream logStream)
        {
            LogStream = logStream;
        }

        private void StateOwnerMain()
        {
            StateOwnerThreadId = Thread.CurrentThread.ManagedThreadId;
            foreach (var action in StateQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (!SessionClosed)
                    {
                        action();
                    }
                }
                catch (Exception e)
                {
                    LogError(e.ToString());
                    CloseSession(false, e);
                }
            }
        }

        private bool PostOnState(Action action)
        {
            if (SessionClosed || StateQueue.IsAddingCompleted)
            {
                return false;
            }
            try
            {
                StateQueue.Add(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void InvokeOnState(Action action)
        {
            if (Thread.CurrentThread.ManagedThreadId == StateOwnerThreadId)
            {
                action();
                return;
            }
            if (SessionClosed || StateQueue.IsAddingCompleted)
            {
                return;
            }

            Exception error = null;
            using (var completed = new ManualResetEventSlim(false))
            {
                var queued = PostOnState(() =>
                {
                    try
                    {
                        if (!SessionClosed)
                        {
                            action();
                        }
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        completed.Set();
                    }
                });
                if (!queued)
                {
                    return;
                }
                completed.Wait();
            }
            if (error != null)
            {
                throw error;
            }
        }

        /*private void SendBreakpoint(string eventType, Breakpoint bp)
        {
            var bpMsg = new DAPBreakpointEvent
            {
                reason = eventType,
                breakpoint = bp.ToDAP()
            };
            Stream.SendEvent("breakpoint", bpMsg);
        }*/

        public void SendOutput(string category, string output)
        {
            var outputMsg = new DAPOutputMessage
            {
                category = category,
                output = output
            };
            Stream.SendEvent("output", outputMsg);
        }

        public void LogError(String message)
        {
            SendOutput("stderr", message + "\r\n");

            if (LogStream != null)
            {
                using (var writer = new StreamWriter(LogStream, Encoding.UTF8, 0x1000, true))
                {
                    writer.WriteLine(message);
                    Console.WriteLine(message);
                }
            }
        }

        private void MessageReceived(DAPMessage message)
        {
            InvokeOnState(() => MessageReceivedOnState(message));
        }

        private void MessageReceivedOnState(DAPMessage message)
        {
            if (message is DAPRequest)
            {
                try
                {
                    HandleRequest(message as DAPRequest);
                }
                catch (RequestFailedException e)
                {
                    Stream.SendReply(message as DAPRequest, e.Message);
                }
                catch (IOException e)
                {
                    Stream.SendReply(message as DAPRequest, e.Message);
                    CloseSession(false, e);
                }
                catch (SocketException e)
                {
                    Stream.SendReply(message as DAPRequest, e.Message);
                    CloseSession(false, e);
                }
                catch (Exception e)
                {
                    LogError(e.ToString());
                    Stream.SendReply(message as DAPRequest, e.ToString());
                    Stream.SendEvent("output", new DAPOutputMessage
                    {
                        category = "important",
                        output = $"Error while handling request '{(message as DAPRequest).command}':\r\n{e.ToString()}"
                    });
                }
            }
            else if (message is DAPEvent)
            {
                HandleEvent(message as DAPEvent);
            }
            else
            {
                throw new InvalidDataException("DAP replies not handled");
            }
        }

        private void InputClosed()
        {
            PostOnState(() => CloseSession(true, null));
        }

        private void CloseSession(bool expected, Exception error)
        {
            if (SessionClosed)
            {
                return;
            }

            SessionClosed = true;
            PairCompatible = false;
            ContextsReady = false;
            InitializedSent = false;
            InitialContexts.Clear();
            ContextStates.Clear();

            DAPRequest pendingLaunch;
            lock (HandshakeLock)
            {
                pendingLaunch = PendingLaunchRequest;
                PendingLaunchRequest = null;
            }
            if (pendingLaunch != null)
            {
                Stream.SendReply(pendingLaunch,
                    new string(new[] { 'd', 'e', 'b', 'u', 'g', 'g', 'e', 'r', '_', 'c', 'l', 'o', 's', 'e', 'd' }));
            }

            foreach (var request in PendingSourceRequests.Values.ToList())
            {
                Stream.SendReply(request, Evaluator.BackendError(StatusCode.StaleContext));
            }
            PendingSourceRequests.Clear();

            if (PendingLoadedSourcesRequest != null)
            {
                Stream.SendReply(PendingLoadedSourcesRequest, Evaluator.BackendError(StatusCode.StaleContext));
                PendingLoadedSourcesRequest = null;
            }

            foreach (var control in PendingControls.Values.ToList())
            {
                Stream.SendReply(control.Request, Evaluator.BackendError(StatusCode.StaleContext));
            }
            PendingControls.Clear();
            Evaluator.InvalidateAll();

            ServerState.Reset();
            ClientState.Reset();

            if (error != null && !expected)
            {
                LogError(error.ToString());
            }
            if (!expected && !TerminationSent)
            {
                TerminationSent = true;
                Stream.SendEvent(new string(new[] { 't', 'e', 'r', 'm', 'i', 'n', 'a', 't', 'e', 'd' }),
                    new DAPTerminatedEvent());
            }

            if (DbgCli != null)
            {
                DbgCli.Close();
            }
            else if (DbgClient != null)
            {
                DbgClient.Close();
            }

            Stream.Close();
            try
            {
                StateQueue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public ThreadState GetThread(int threadId, bool allowInactive = false)
        {
            if (threadId == ServerThreadId)
            {
                if (!ServerState.Active && !allowInactive)
                {
                    throw new RequestFailedException("Requested server thread, but there is no active server context!");
                }

                return ServerState;
            }
            else if (threadId == ClientThreadId)
            {
                if (!ClientState.Active && !allowInactive)
                {
                    throw new RequestFailedException("Requested client thread, but there is no active client context!");
                }

                return ClientState;
            }
            else
            {
                throw new RequestFailedException($"Requested unknown thread ID {threadId}!");
            }
        }

        public ThreadState GetThread(DbgContext context)
        {
            if (context == DbgContext.Server)
            {
                return ServerState;
            }
            else
            {
                return ClientState;
            }
        }

        public int GetThreadId(DbgContext context)
        {
            if (context == DbgContext.Server)
            {
                return ServerThreadId;
            }
            else
            {
                return ClientThreadId;
            }
        }

        private void OnBackendConnected(BkConnectResponse response)
        {
            DAPRequest pendingLaunch;
            string mismatchCode;
            lock (HandshakeLock)
            {
                if (PendingLaunchRequest == null)
                {
                    return;
                }
                BackendHandshake = response;
                mismatchCode = GetMismatchCode(response);
                PairCompatible = mismatchCode == null;
                pendingLaunch = PendingLaunchRequest;
                PendingLaunchRequest = null;
            }

            if (mismatchCode != null)
            {
                Stream.SendReply(pendingLaunch,
                    $"BG3SE debugger backend is incompatible: {mismatchCode}",
                    new DAPLaunchResponse
                    {
                        bg3se = new DAPBG3SEHandshake
                        {
                            backendProtocolVersion = response?.ProtocolVersion,
                            mismatchCode = mismatchCode
                        }
                    });
                CloseSession(false, new InvalidDataException(mismatchCode));
            }
            else
            {
                Stream.SendReply(pendingLaunch, new DAPLaunchResponse
                {
                    bg3se = new DAPBG3SEHandshake
                    {
                        backendProtocolVersion = response.ProtocolVersion
                    }
                });
                CompleteBackendReady();
            }
        }

        private string GetMismatchCode(BkConnectResponse response)
        {
            if (response == null || response.ProtocolVersion != DBGProtocolVersion)
            {
                return "protocol_mismatch";
            }
            return null;
        }

        private bool FailPendingHandshake(Exception error)
        {
            DAPRequest pendingLaunch;
            BkConnectResponse backendHandshake;
            lock (HandshakeLock)
            {
                if (PendingLaunchRequest == null)
                {
                    return false;
                }
                pendingLaunch = PendingLaunchRequest;
                backendHandshake = BackendHandshake;
                PendingLaunchRequest = null;
            }
            var version = backendHandshake?.ProtocolVersion;
            Stream.SendReply(pendingLaunch,
                $"BG3SE debugger handshake failed (backend protocol version: {version})",
                new DAPLaunchResponse
                {
                    bg3se = new DAPBG3SEHandshake { backendProtocolVersion = version }
                });
            LogError(error.ToString());
            return true;
        }

        private void OnBreakpointTriggered(BkBreakpointTriggered bp)
        {
            var state = GetThread(bp.Context);

            state.Stack = new List<StackFrame>();
            foreach (var frame in bp.Stack)
            {
                var stkFrame = new StackFrame();
                stkFrame.Function = frame.Function;
                if (frame.Source == "=[C]")
                {
                    stkFrame.Source = "(C++ Code)";
                    stkFrame.Path = null;
                    stkFrame.Line = 0;
                    stkFrame.ScopeFirstLine = 0;
                    stkFrame.ScopeLastLine = 0;
                }
                else
                {
                    if (stkFrame.Path == "")
                    {
                        stkFrame.Source = "(Inline Code)";
                        stkFrame.Path = null;
                    }
                    else
                    {
                        stkFrame.Source = Path.GetFileName(frame.Source);
                        stkFrame.Path = frame.Path.Replace("/", "\\");
                    }
                    stkFrame.Line = frame.Line;
                    stkFrame.ScopeFirstLine = frame.ScopeFirstLine;
                    stkFrame.ScopeLastLine = frame.ScopeLastLine;
                }
                state.Stack.Add(stkFrame);
            }

            state.Stopped = true;

            var stopped = new DAPStoppedEvent
            {
                threadId = GetThreadId(bp.Context)
            };

            switch (bp.Reason)
            {
                case BkBreakpointTriggered.Types.Reason.Breakpoint:
                    stopped.reason = "breakpoint";
                    break;

                case BkBreakpointTriggered.Types.Reason.Exception:
                    stopped.reason = "exception";
                    stopped.description = "Paused on exception";
                    stopped.text = bp.Message;
                    break;

                case BkBreakpointTriggered.Types.Reason.Pause:
                    stopped.reason = "pause";
                    break;

                case BkBreakpointTriggered.Types.Reason.Step:
                    stopped.reason = "step";
                    break;

                default:
                    stopped.reason = "breakpoint";
                    break;
            }

            Stream.SendEvent("stopped", stopped);
        }

        private void OnContextUpdated(BkContextUpdated msg)
        {
            if (msg.Context != DbgContext.Server && msg.Context != DbgContext.Client)
            {
                CloseSession(false, new InvalidDataException());
                return;
            }
            if (msg.Status != BkContextUpdated.Types.Status.Loaded
                && msg.Status != BkContextUpdated.Types.Status.Unloaded)
            {
                CloseSession(false, new InvalidDataException());
                return;
            }
            var ctx = msg.Context == DbgContext.Server ? ServerState : ClientState;
            var active = msg.Status == BkContextUpdated.Types.Status.Loaded;
            var initial = !InitialContexts.Contains(msg.Context);

            if (ContextStates.TryGetValue(msg.Context, out var previous))
            {
                if (previous == msg.Status)
                {
                    return;
                }
                if (!ContextsReady)
                {
                    CloseSession(false, new InvalidDataException("conflicting context lifecycle event"));
                    return;
                }
            }

            if (initial)
            {
                InitialContexts.Add(msg.Context);
            }

            if (ctx.Active != active)
            {
                ctx.Reset();
            }

            ctx.Active = active;
            ContextStates[msg.Context] = msg.Status;
            Stream.SendEvent("bg3se/context", new DAPContextEvent
            {
                context = msg.Context == DbgContext.Server ? "server" : "client",
                state = active ? "loaded" : "unloaded",
                initial = initial
            });
            if (!active)
            {
                InvalidateContext(msg.Context);
            }

            if (!ContextsReady && InitialContexts.Contains(DbgContext.Server)
                && InitialContexts.Contains(DbgContext.Client))
            {
                ContextsReady = true;
                Stream.SendEvent("bg3se/contextsReady", new DAPContextsReadyEvent());
            }
        }

        private void InvalidateContext(DbgContext context)
        {
            Evaluator.InvalidateContext(context);

            var controls = PendingControls
                .Where(pair => pair.Value.Context == context)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var seq in controls)
            {
                var pending = PendingControls[seq];
                PendingControls.Remove(seq);
                Stream.SendReply(pending.Request, Evaluator.BackendError(StatusCode.StaleContext));
            }

            foreach (var request in PendingSourceRequests.Values.ToList())
            {
                Stream.SendReply(request, Evaluator.BackendError(StatusCode.StaleContext));
            }
            PendingSourceRequests.Clear();
            if (PendingLoadedSourcesRequest != null)
            {
                Stream.SendReply(PendingLoadedSourcesRequest, Evaluator.BackendError(StatusCode.StaleContext));
                PendingLoadedSourcesRequest = null;
            }
        }

        private void OnModInfo(BkModInfoResponse msg)
        {
            Modules = msg.Module.Select(mod => new ModuleInfo
            {
                Guid = mod.Uuid,
                Name = mod.Name,
                Author = mod.Author,
                Path = mod.Path
            }).ToList();

            SourceFiles = msg.Source.Select(source => new SourceInfo{
                    Path = source.Path,
                    Name = source.Name
                }
            ).ToList();

            if (PendingLoadedSourcesRequest != null)
            {
                SendLoadedSources(PendingLoadedSourcesRequest);
                PendingLoadedSourcesRequest = null;
            }
        }

        private void OnDebugOutput(BkDebugOutput msg)
        {
            // Strip unnecessary prefixes that were meant for editor log output
            var message = msg.Message;
            if (message.StartsWith("[Osiris] "))
            {
                message = message.Substring(9);
                if (message.StartsWith("{W}" ) || message.StartsWith("{I}") || message.StartsWith("{E}"))
                {
                    message = message.Substring(4);
                }
            }

            switch (msg.Severity)
            {
                case BkDebugOutput.Types.Severity.LevelDebug:
                case BkDebugOutput.Types.Severity.LevelInfo:
                    SendOutput("stdout", message  + "\r\n");
                    break;

                case BkDebugOutput.Types.Severity.LevelWarning:
                    SendOutput("console", message + "\r\n");
                    break;

                case BkDebugOutput.Types.Severity.LevelError:
                    SendOutput("stderr", message + "\r\n");
                    break;
            }
        }

        private void OnResults(UInt32 seq, BkResult msg)
        {
            if (PendingSourceRequests.TryGetValue(seq, out DAPRequest sourceReq))
            {
                PendingSourceRequests.Remove(seq);
                if (msg.StatusCode == StatusCode.StaleContext)
                {
                    Stream.SendReply(sourceReq, Evaluator.BackendError(msg.StatusCode));
                    return;
                }
                Stream.SendReply(sourceReq, $"Fetch failed: {msg.StatusCode}");
                return;
            }

            if (PendingControls.TryGetValue(seq, out PendingControl control))
            {
                PendingControls.Remove(seq);
                if (msg.StatusCode != StatusCode.Success)
                {
                    Stream.SendReply(control.Request, Evaluator.BackendError(msg.StatusCode));
                    return;
                }

                var state = GetThread(control.Context);
                if (control.Action != DbgContinue.Types.Action.Pause)
                {
                    state.Stopped = false;
                    state.Stack = null;
                    Stream.SendEvent(new string(new[] { 'c', 'o', 'n', 't', 'i', 'n', 'u', 'e', 'd' }),
                        new DAPContinuedEvent
                        {
                            threadId = GetThreadId(control.Context),
                            allThreadsContinued = false
                        });
                }
                Stream.SendReply(control.Request, new DAPContinueResponse
                {
                    allThreadsContinued = false
                });
                return;
            }

            Evaluator.OnResultsReceived(seq, msg);
        }

        private void CompleteBackendReady()
        {
            lock (HandshakeLock)
            {
                if (InitializedSent)
                {
                    return;
                }
                InitializedSent = true;
            }
            SendInitialized("Debugger backend ready\r\n");
        }

        private void SendInitialized(string notice)
        {
            DbgCli.SendUpdateSettings(Config.breakOnError, Config.breakOnGenericError);

            SendOutput("console", notice);

            var initializedEvt = new DAPInitializedEvent();
            Stream.SendEvent("initialized", initializedEvt);
        }
        private void OnSourceResponse(UInt32 seq, BkSourceResponse msg)
        {
            if (!PendingSourceRequests.TryGetValue(seq, out DAPRequest dapRequest))
            {
                return;
            }
            PendingSourceRequests.Remove(seq);

            var reply = new DAPSourceResponse
            {
                content = msg.Body
            };
            Stream.SendReply(dapRequest, reply);
        }

        private void HandleInitializeRequest(DAPRequest request, DAPInitializeRequest init)
        {
            var reply = new DAPCapabilities
            {
                supportsConfigurationDoneRequest = true,
                supportsEvaluateForHovers = true,
                supportsModulesRequest = true,
                supportsLoadedSourcesRequest = true
            };
            Stream.SendReply(request, reply);
        }

        private void DebugThreadMain()
        {
            try
            {
                DbgClient.RunLoop();
            }
            catch (Exception e)
            {
                PostOnState(() =>
                {
                    FailPendingHandshake(e);
                    CloseSession(false, e);
                });
            }
        }

        private void StartBackendReader()
        {
            DbgThread = new Thread(new ThreadStart(DebugThreadMain));
            DbgThread.Start();
        }

        private void HandleLaunchRequest(DAPRequest request, DAPLaunchRequest launch)
        {
            Config = launch.dbgOptions;

            if (launch.backendHost == null || launch.backendPort == 0)
            {
                throw new RequestFailedException("'backendHost' and 'backendPort' launch configuration variables must be set!");
            }

            if (Config == null)
            {
                Config = new DAPCustomConfiguration();
            }

            if (launch.noDebug)
            {
                throw new RequestFailedException("Cannot attach to game with debugging disabled");
            }

            BackendHost = launch.backendHost;
            BackendPort = launch.backendPort;

            try
            {
                DbgClient = new AsyncProtobufClient(launch.backendHost, launch.backendPort);
            }
            catch (SocketException e)
            {
                throw new RequestFailedException("Could not connect to Lua debugger backend: " + e.Message);
            }

            DbgCli = new DebuggerClient(DbgClient)
            {
                OnBackendConnected = response => PostOnState(() => OnBackendConnected(response)),
                OnBreakpointTriggered = bp => PostOnState(() => OnBreakpointTriggered(bp)),
                OnEvaluateFinished = (seq, msg) => PostOnState(() => Evaluator.OnEvaluateFinished(seq, msg)),
                OnContextUpdated = msg => PostOnState(() => OnContextUpdated(msg)),
                OnModInfo = msg => PostOnState(() => OnModInfo(msg)),
                OnDebugOutput = msg => PostOnState(() => OnDebugOutput(msg)),
                OnResults = (seq, msg) => PostOnState(() => OnResults(seq, msg)),
                OnSourceResponse = (seq, msg) => PostOnState(() => OnSourceResponse(seq, msg)),
                OnGetVariablesFinished = (seq, msg) => PostOnState(() => Evaluator.OnGetVariablesFinished(seq, msg))
            };
            if (LogStream != null)
            {
                DbgCli.EnableLogging(LogStream);
            }
            
            lock (HandshakeLock)
            {
                PendingLaunchRequest = request;
                PairCompatible = false;
                BackendHandshake = null;
                InitializedSent = false;
            }

            try
            {
                DbgCli.SendConnectRequest(DBGProtocolVersion);
                StartBackendReader();
            }
            catch (Exception e)
            {
                FailPendingHandshake(e);
            }

        }

        private void SendBreakpointUpdate()
        {
            var bps = new List<BreakpointInfo>();
            foreach (var fileBps in Breakpoints)
            {
                foreach (var bp in fileBps.Value)
                {
                    bps.Add(bp.Value);
                }
            }

            DbgCli.SendSetBreakpoints(bps);
        }

        private void HandleSetBreakpointsRequest(DAPRequest request, DAPSetBreakpointsRequest breakpoints)
        {
            var path = breakpoints.source.path.Length > 0 ? breakpoints.source.path : breakpoints.source.name;

            var reply = new DAPSetBreakpointsResponse
            {
                breakpoints = new List<DAPBreakpoint>()
            };

            var fileBps = new Dictionary<int, BreakpointInfo>();
            foreach (var breakpoint in breakpoints.breakpoints)
            {
                var bp = new BreakpointInfo();
                bp.Id = NextBreakpointId++;
                bp.Path = path;
                bp.Line = breakpoint.line;
                fileBps.Add(bp.Line, bp);

                var dapBp = new DAPBreakpoint();
                dapBp.id = bp.Id;
                dapBp.verified = true; // TODO - verify with backend?
                dapBp.source = breakpoints.source;
                dapBp.line = bp.Line;
                reply.breakpoints.Add(dapBp);
            }

            Breakpoints[path] = fileBps;
            SendBreakpointUpdate();

            Stream.SendReply(request, reply);
        }

        private void HandleConfigurationDoneRequest(DAPRequest request, DAPEmptyPayload msg)
        {
            Stream.SendReply(request, new DAPEmptyPayload());
        }

        private void HandleThreadsRequest(DAPRequest request, DAPEmptyPayload msg)
        {
            var reply = new DAPThreadsResponse
            {
                threads = new List<DAPThread>()
            };

            reply.threads.Add(new DAPThread
            {
                id = ServerThreadId,
                name = "Lua Server"
            });

            reply.threads.Add(new DAPThread
            {
                id = ClientThreadId,
                name = "Lua Client"
            });

            Stream.SendReply(request, reply);
        }

        private void HandleStackTraceRequest(DAPRequest request, DAPStackFramesRequest msg)
        {
            var state = GetThread(msg.threadId);

            if (!state.Stopped)
            {
                throw new RequestFailedException("Cannot get stack when thread is not stopped");
            }

            int startFrame = msg.startFrame == null ? 0 : (int)msg.startFrame;
            int levels = (msg.levels == null || msg.levels == 0) ? state.Stack.Count : (int)msg.levels;
            int lastFrame = Math.Min(startFrame + levels, state.Stack.Count);

            // Count total sendable frames
            int numFrames = 0;
            foreach (var frame in state.Stack)
            {
                if (!Config.omitCppFrames || frame.Path != null)
                {
                    numFrames++;
                }
            }

            var frames = new List<DAPStackFrame>();
            for (var i = startFrame; i < lastFrame; i++)
            {
                var frame = state.Stack[i];
                if (Config.omitCppFrames && frame.Path == null)
                {
                    continue;
                }

                var dapFrame = new DAPStackFrame();
                dapFrame.id = (msg.threadId << 16) | i;
                // TODO DAPStackFrameFormat for name formatting
                dapFrame.name = frame.Function;
                if (frame.Path != null || frame.Source != null)
                {
                    dapFrame.source = new DAPSource
                    {
                        name = frame.Source,
                        path = frame.Path?.Replace("/", "\\") ?? frame.Source
                    };
                    dapFrame.line = frame.Line;
                    dapFrame.column = (frame.Line > 0) ? 1 : 0;
                }

                // TODO presentationHint
                frames.Add(dapFrame);
            }

            var reply = new DAPStackFramesResponse
            {
                stackFrames = frames,
                totalFrames = numFrames
            };
            Stream.SendReply(request, reply);
        }

        private void SetScopeRange(DAPScope scope, StackFrame frame)
        {
            // Send location information for rule-local scopes.
            // If the scope location is missing, the value of local variables will be displayed in 
            // every rule that has variables with the same name.
            // This restricts them so they're only displayed in the rule that the stack frame belongs to.
            if (frame.ScopeFirstLine != 0)
            {
                scope.source = new DAPSource
                {
                    name = frame.Source,
                    path = frame.Path?.Replace("/", "\\") ?? frame.Source
                };
                scope.line = frame.ScopeFirstLine;
                scope.column = 1;
                scope.endLine = frame.ScopeLastLine;
                scope.endColumn = 1;
            }
        }

        private void HandleScopesRequest(DAPRequest request, DAPScopesRequest msg)
        {
            var threadId = msg.frameId >> 16;
            var frameIndex = msg.frameId & 0xffff;

            var state = GetThread(threadId);

            if (!state.Stopped)
            {
                throw new RequestFailedException("Cannot get scopes when thread is not stopped");
            }

            if (frameIndex < 0 || frameIndex >= state.Stack.Count)
            {
                throw new RequestFailedException("Requested scopes for unknown frame");
            }

            var frame = state.Stack[frameIndex];
            var stackScope = new DAPScope
            {
                name = "Locals",
                variablesReference = Evaluator.MakeStackRef(threadId, frameIndex, 0),
                namedVariables = 0, // TODO - get hinted named/indexed variable count,
                indexedVariables = 0,
                expensive = false
            };
            SetScopeRange(stackScope, frame);

            var upvalueScope = new DAPScope
            {
                name = "Upvalues",
                variablesReference = Evaluator.MakeStackRef(threadId, frameIndex, 1),
                namedVariables = 0,
                indexedVariables = 0,
                expensive = false
            };
            SetScopeRange(upvalueScope, frame);

            var scopes = new List<DAPScope> { stackScope, upvalueScope };

            var reply = new DAPScopesResponse
            {
                scopes = scopes
            };
            Stream.SendReply(request, reply);
        }

        private void SendContinue(DbgContext context, DbgContinue.Types.Action action)
        {
            DbgCli.SendContinue(context, action);
        }

        private void HandleContinueRequest(DAPRequest request, DAPContinueRequest msg, DbgContinue.Types.Action action)
        {
            var state = GetThread(msg.threadId, true);

            if (action == DbgContinue.Types.Action.Pause)
            {
                if (state.Stopped)
                {
                    throw new RequestFailedException("Already stopped");
                }
            }
            else
            {
                if (!state.Stopped)
                {
                    throw new RequestFailedException("Already running");
                }

            }

            var pending = new PendingControl
            {
                Request = request,
                Context = state.Context,
                Action = action
            };
            DbgCli.SendContinue(state.Context, action, seq => PendingControls.Add(seq, pending));
        }

        private void HandleEvaluateRequest(DAPRequest request, DAPEvaulateRequest msg)
        {
            if (msg.expression == "reset" || msg.expression == "reset client" || msg.expression == "reset server")
            {
                if (msg.expression == "reset")
                {
                    DbgCli.SendReset(DbgContext.Client);
                    DbgCli.SendReset(DbgContext.Server);
                }
                else if (msg.expression == "reset client")
                {
                    DbgCli.SendReset(DbgContext.Client);
                }
                else if (msg.expression == "reset server")
                {
                    DbgCli.SendReset(DbgContext.Server);
                }

                DAPEvaluateResponse evalResponse = new DAPEvaluateResponse
                {
                    result = "",
                    namedVariables = 0,
                    indexedVariables = 0,
                    variablesReference = 0,
                };
                Stream.SendReply(request, evalResponse);
            }
            else
            {
                Evaluator.OnDAPEvaluateRequested(request, msg);
            }
        }

        private void HandleSourceRequest(DAPRequest request, DAPSourceRequest req)
        {
            if (req.source == null || req.source.path == null)
            {
                throw new RequestFailedException("Cannot handle source requests without a path");
            }

            var path = req.source.path.Length > 0 ? req.source.path : req.source.name;
            DbgCli.SendSourceRequest(path, seq => PendingSourceRequests.Add(seq, request));
        }

        private void HandleLoadedSourcesRequest(DAPRequest request, DAPLoadedSourcesRequest req)
        {
            if (SourceFiles == null)
            {
                PendingLoadedSourcesRequest = request;
            }
            else
            {
                SendLoadedSources(request);
            }
        }

        private void SendLoadedSources(DAPRequest request)
        {
            var reply = new DAPLoadedSourcesResponse
            {
                sources = SourceFiles.Select(source => new DAPSource
                {
                    path = (source.Path.Length > 0) ? source.Path.Replace("/", "\\") : source.Name,
                    name = (source.Name.Length > 0) ? source.Name : ""
                }).ToList()
            };
            Stream.SendReply(request, reply);
        }

        private void HandleModulesRequest(DAPRequest request, DAPModulesRequest req)
        {
            int startIdx = req.startModule;
            int lastIdx;
            if (req.moduleCount == 0)
            {
                lastIdx = Modules.Count;
            } 
            else
            {
                lastIdx = Math.Min(startIdx + req.moduleCount, Modules.Count);
            }

            var reply = new DAPModulesResponse
            {
                modules = new List<DAPModule>(),
                totalModules = Modules.Count
            };

            for (int i = startIdx; i < lastIdx; i++)
            {
                var mod = Modules[i];
                reply.modules.Add(new DAPModule
                {
                    id = mod.Guid,
                    name = mod.Name,
                    path = mod.Path
                });
            }

            Stream.SendReply(request, reply);
        }

        private void HandleDisconnectRequest(DAPRequest request, DAPDisconnectRequest msg)
        {
            var reply = new DAPEmptyPayload();
            Stream.SendReply(request, reply);
            CloseSession(true, null);
        }

        private void HandleRequest(DAPRequest request)
        {
            switch (request.command)
            {
                case "initialize":
                    HandleInitializeRequest(request, request.arguments as DAPInitializeRequest);
                    break;

                case "launch":
                    HandleLaunchRequest(request, request.arguments as DAPLaunchRequest);
                    break;

                case "setBreakpoints":
                    HandleSetBreakpointsRequest(request, request.arguments as DAPSetBreakpointsRequest);
                    break;

                case "configurationDone":
                    HandleConfigurationDoneRequest(request, request.arguments as DAPEmptyPayload);
                    break;

                case "threads":
                    HandleThreadsRequest(request, request.arguments as DAPEmptyPayload);
                    break;

                case "stackTrace":
                    HandleStackTraceRequest(request, request.arguments as DAPStackFramesRequest);
                    break;

                case "scopes":
                    HandleScopesRequest(request, request.arguments as DAPScopesRequest);
                    break;

                case "variables":
                    Evaluator.OnDAPVariablesRequested(request, request.arguments as DAPVariablesRequest);
                    break;

                case "continue":
                    HandleContinueRequest(request, request.arguments as DAPContinueRequest,
                        DbgContinue.Types.Action.Continue);
                    break;

                case "next":
                    HandleContinueRequest(request, request.arguments as DAPContinueRequest,
                        DbgContinue.Types.Action.StepOver);
                    break;

                case "stepIn":
                    HandleContinueRequest(request, request.arguments as DAPContinueRequest,
                        DbgContinue.Types.Action.StepInto);
                    break;

                case "stepOut":
                    HandleContinueRequest(request, request.arguments as DAPContinueRequest,
                        DbgContinue.Types.Action.StepOut);
                    break;

                case "pause":
                    HandleContinueRequest(request, request.arguments as DAPContinueRequest,
                        DbgContinue.Types.Action.Pause);
                    break;

                case "evaluate":
                    HandleEvaluateRequest(request, request.arguments as DAPEvaulateRequest);
                    break;

                case "source":
                    HandleSourceRequest(request, request.arguments as DAPSourceRequest);
                    break;

                case "modules":
                    HandleModulesRequest(request, request.arguments as DAPModulesRequest);
                    break;

                case "loadedSources":
                    HandleLoadedSourcesRequest(request, request.arguments as DAPLoadedSourcesRequest);
                    break;

                case "disconnect":
                    HandleDisconnectRequest(request, request.arguments as DAPDisconnectRequest);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported DAP request: {request.command}");
            }
        }

        private void HandleEvent(DAPEvent evt)
        {
            throw new InvalidOperationException($"Unsupported DAP event: {evt.@event}");
        }
    }
}



