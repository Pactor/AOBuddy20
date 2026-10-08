using System.Collections.Concurrent;
using System.Net;
using AOSharp.Clientless.Common;
using AOSharp.Common;
using AOSharp.Common.GameData;
using Serilog;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.SystemMessages;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.Serializers;
using Stateless;
using MessagingStreamReader = SmokeLounge.AOtomation.Messaging.Serialization.StreamReader;

namespace AOSharp.Clientless.Net;

public class SessionCookie
{
    public uint Cookie1;
    public uint Cookie2;
}

public class NetworkSession
{
    private readonly HeaderSerializer _headerSerializer = new HeaderSerializer();

    private readonly ConcurrentQueue<byte[]> _inboundPacketQueue = new ConcurrentQueue<byte[]>();

    // Distinct unparseable packets already logged (key = n3type:length), so a message the stock
    // serializer can't read is reported ONCE with its full hex instead of spamming a stack trace
    // every time the server sends it. See ProcessCachedPacket's catch.
    private readonly HashSet<string> _loggedBadPackets = new HashSet<string>();

    private readonly ILogger _logger;
    private readonly Dictionary<N3MessageType, Action<N3Message>> _n3MsgCallbacks;
    private readonly MessageSerializer _serializer = new MessageSerializer();

    private readonly Dictionary<SystemMessageType, Action<SystemMessage>> _sysMsgCallbacks;

    // private System.Timers.Timer _reconnectTimer; - never used
    // private System.Timers.Timer _pingTimer; - never used

    internal Action<StateMachine<State, Trigger>.Transition> NetworkStateChanged;
    private Dictionary<N3MessageType, Action<N3Message>> _internalN3MsgCallbacks;
    private Dictionary<SystemMessageType, Action<SystemMessage>> _internalSysMsgCallbacks;
    private ushort _messageId = 1;

    // SEND FROM ANY THREAD (owner, 2026-10-01: "make it threadsafe. Important part is the packet id").
    // AOBuddy20 runs its movement cycle on a dedicated thread, so Send is called from there while the
    // update loop answers pings and playstate echoes. The message id was a plain read-increment-write
    // (two senders could take the same id and the server drops misordered duplicates), and the
    // serializer is shared mutable state. One lock around the whole send keeps the ids unique and
    // each packet contiguous; it is held for microseconds.
    private readonly object _sendLock = new object();

    private SessionCookie _sessionCookie;
    private NetworkStateMachine _stateMachine;
    private ZlibTcpClient _tcpClient;

    internal NetworkSession(ILogger logger, Dictionary<SystemMessageType, Action<SystemMessage>> sysMsgCallbacks,
        Dictionary<N3MessageType, Action<N3Message>> n3MsgCallbacks)
    {
        _logger = logger;
        InitializeStateMachine();
        RegisterInternalSystemMessageHandlers();
        RegisterInternalN3MessageHandlers();

        _sysMsgCallbacks = sysMsgCallbacks;
        _n3MsgCallbacks = n3MsgCallbacks;
    }

    internal bool InPlay => _stateMachine.IsInState(State.InPlay);
    internal bool Connected => _tcpClient is { Connected: true };

    internal void Update()
    {
        while (_inboundPacketQueue.TryDequeue(out var packet))
        {
            ProcessCachedPacket(packet);
        }
    }

    public void Connect()
    {
        _logger.Debug("Requesting dimension info..");

        try
        {
            var dimensionInfo = Client.Dimension == Dimension.RubiKa ? DimensionInfo.RubiKa : DimensionInfo.RubiKa2019;
            var loginHandlerEndpoint = new IPEndPoint(Dns.GetHostEntry(dimensionInfo.GameServerEndpoint.Host).AddressList[0],
                dimensionInfo.GameServerEndpoint.Port);
            _stateMachine.Fire(_stateMachine.ConnectTrigger, loginHandlerEndpoint);
        }
        catch (WebException ex)
        {
            _logger.Error($"Failed to retrieve dimension info. {ex}");
            _stateMachine.Fire(Trigger.FailedToRetreiveDimensionInfo);
        }
    }

    private void Connect(IPEndPoint endpoint)
    {
        _logger.Debug($"Connecting to {endpoint}");

        if (_tcpClient != null && _tcpClient.Connected)
        {
            _tcpClient.Close();
        }

        _tcpClient = new ZlibTcpClient(_logger);
        _tcpClient.Disconnected += (e, p) => _stateMachine.Fire(Trigger.Disconnect);
        _tcpClient.PacketRecv += (e, p) => _inboundPacketQueue.Enqueue(p);
        _tcpClient.BeginConnect(endpoint.Address, endpoint.Port, ConnectCallback, endpoint);
    }

    private void ConnectCallback(IAsyncResult result)
    {
        try
        {
            _tcpClient.EndConnect(result);
            _stateMachine.Fire(Trigger.OnTcpConnected);
        }
        catch (Exception exception)
        {
            var endpoint = result.AsyncState as IPEndPoint;

            _logger.Debug($"Failed to connect to {endpoint} -- {exception}");

            _stateMachine.Fire(Trigger.OnTcpConnectError);
        }
    }

    public void Disconnect()
    {
        _stateMachine.Fire(Trigger.Disconnect);
    }

    public void Send(MessageBody messageBody)
    {
        if (messageBody is N3Message n3Message)
        {
            n3Message.Identity = new Identity(IdentityType.SimpleChar, Client.LocalDynelId);
        }

        var aoMessage = new AOMessage
        {
            Body = messageBody,
            Header = new Header
            {
                PacketType = messageBody.PacketType,
                Sender = Client.LocalDynelId,
                Receiver = messageBody.PacketType == PacketType.SystemMessage ? 1 : 2,
            },
        };

        Send(aoMessage);
    }

    public void Send(AOMessage aoMessage)
    {
        lock (_sendLock)
        {
            aoMessage.Header.MessageId = _messageId;

            using (var stream = new MemoryStream())
            {
                _serializer.Serialize(stream, aoMessage);
                var bytes = stream.ToArray();
                Client.RaisePacketRaw(bytes, false);
                _tcpClient.Send(bytes);
            }

            _messageId++;

            if (_messageId == 0xFFFF)
            {
                _messageId = 1;
            }
        }
    }

    private void ProcessCachedPacket(byte[] packet)
    {
        Client.RaisePacketRaw(packet, true);
        try
        {
            // WORKAROUND (AOSharpSDK 1.0.89): the SimpleCharFullUpdate reader
            // baked into the NuGet AOSharp.Common.dll is an older, incomplete
            // version that throws EndOfStreamException on pet-type / complex
            // NPC spawns, so those dynels never register. There is no API to
            // replace a serializer, so detect those packets up front and parse
            // them with our corrected reader (see SimpleCharFullUpdateReader),
            // then let the result flow through the normal dispatch below. Every
            // other packet takes the unchanged MessageSerializer path.
            AOMessage aoMessage;
            if (!TryDeserializeSimpleCharFullUpdate(packet, out aoMessage))
            {
                // WORKAROUND (AOSharpSDK 1.0.89): the ChestFullUpdate reader baked into the NuGet
                // AOSharp.Common.dll is an older, broken version that throws OverflowException on the
                // mission / corpse containers the server sends on zone-in (a huge count is read from
                // the wrong offset). The correct field layout is known (OmniCell's
                // ChestItemFullUpdateMessage), but the bot doesn't use chest contents, so we simply
                // DROP these packets rather than let the broken serializer throw and spam the console.
                // When looting is needed later, add a corrected reader here like the SimpleChar one.
                if (IsN3MessageType(packet, N3MessageType.ChestFullUpdate))
                {
                    // Still not deserialized, but handed on raw: a mission's find-item target can be one of
                    // these containers (quest record target 0xC74E, 2026-09-23 22:10), and dropping them left
                    // the bot unable to see it.
                    Client.RaiseChestFullUpdateRaw(packet);
                    return;
                }

                if (IsN3MessageType(packet, N3MessageType.Action))
                {
                    Client.RaiseActionRaw(packet);
                }

                if (IsN3MessageType(packet, N3MessageType.DoorFullUpdate))
                {
                    Client.RaiseDoorFullUpdateRaw(packet);
                }

                // SpellList: a nano was uploaded/learned mid-session. The stock serializer leaves this
                // message empty (its body is undefined), so learned nanos never reached SpellList and the
                // bot wouldn't summon e.g. a just-learned heal pet until a relog. Pull the added nano out
                // and add it live, then drop the packet (nothing else consumes it).
                if (IsN3MessageType(packet, N3MessageType.SpellList))
                {
                    TryApplySpellListNano(packet);
                    return;
                }

                // FullCharacter: try the stock serializer first (correct for the common no-pet case); only
                // fall back to the corrected reader when it throws (a pet is up — see FullCharacterReader).
                if (!TryDeserializeFullCharacter(packet, out aoMessage))
                {
                    aoMessage = _serializer.Deserialize(packet);
                }
            }

            if (aoMessage == null)
            {
                return;
            }

            if (aoMessage.Header.Sender != Client.ServerId)
            {
                Client.ServerId = aoMessage.Header.Sender;
            }

            Client.MessageReceived?.Invoke(null, aoMessage);

            if (aoMessage.Header.PacketType == PacketType.InitiateCompressionMessage)
            {
                OnInitiateCompressionMessage();
            }
            else if (aoMessage.Header.PacketType == PacketType.PingMessage)
            {
                Pong(aoMessage);
            }
            else if (aoMessage.Header.PacketType == PacketType.SystemMessage)
            {
                var sysMsg = (SystemMessage)aoMessage.Body;

                if (_sysMsgCallbacks.TryGetValue(sysMsg.SystemMessageType, out var callback))
                {
                    callback.Invoke(sysMsg);
                }

                if (_internalSysMsgCallbacks.TryGetValue(sysMsg.SystemMessageType, out var internalCallback))
                {
                    internalCallback.Invoke(sysMsg);
                }
            }
            else if (aoMessage.Header.PacketType == PacketType.N3Message)
            {
                Client.PacketReceived?.Invoke(null, packet);

                var n3Msg = (N3Message)aoMessage.Body;

                if (_n3MsgCallbacks.TryGetValue(n3Msg.N3MessageType, out var callback))
                {
                    callback.Invoke(n3Msg);
                }

                if (_internalN3MsgCallbacks.TryGetValue(n3Msg.N3MessageType, out var internalCallback))
                {
                    internalCallback.Invoke(n3Msg);
                }
            }
        }
        catch (Exception dropEx)
        {
            // A message the stock serializer can't parse (the bundled AOSharp.Common has incomplete
            // coverage for some pet/combat messages). DROP it so the bot keeps running, and log ONE
            // concise line per distinct message — its N3 type id, length and full hex — instead of a
            // repeating stack trace. That gives a clean capture so a corrected reader can be added for
            // the specific type (the SimpleCharFullUpdateReader workaround is the template).
            try
            {
                var typeId = 0;
                if (packet != null && packet.Length >= 20)
                {
                    using (var ms = new MemoryStream(packet))
                    using (var r = new MessagingStreamReader(ms))
                    {
                        r.Position = 16;
                        typeId = r.ReadInt32();
                    }
                }

                var key = $"{typeId:X8}:{(packet == null ? 0 : packet.Length)}";
                if (_loggedBadPackets.Add(key))
                {
                    // The exception used to be DISCARDED here, so every "fix" to a reader was a guess at
                    // which field broke. Log where it actually threw: type, message, and the deepest
                    // frame (file+line) — that names the exact read that failed.
                    var root = dropEx;
                    while (root.InnerException != null)
                    {
                        root = root.InnerException;
                    }

                    // FIRST line = innermost frame (where it threw); LastOrDefault here named the outermost,
                    // ProcessCachedPacket itself, every time.
                    var frame = (root.StackTrace ?? "").Split('\n').FirstOrDefault(s => s.Contains(" in "))?.Trim() ??
                                (root.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "?";
                    _logger.Error(
                        $"Dropping unparseable packet: n3type=0x{typeId:X8} len={(packet == null ? 0 : packet.Length)} EX={root.GetType().Name}: {root.Message} AT {frame}");
                    _logger.Error($"  hex={(packet == null ? "" : packet.ToHexString())}");
                }
            }
            catch
            {
                /* logging must never throw */
            }
        }
    }

    /// <summary>
    ///     If <paramref name="packet" /> is an N3 SimpleCharFullUpdate, parse it with
    ///     the corrected reader and return true; otherwise return false so the caller
    ///     falls back to the stock MessageSerializer. See SimpleCharFullUpdateReader
    ///     for why this workaround exists.
    /// </summary>
    private bool TryDeserializeSimpleCharFullUpdate(byte[] packet, out AOMessage aoMessage)
    {
        aoMessage = null;

        // Need at least a 16-byte header plus the 4-byte N3 message type.
        if (packet == null || packet.Length < 20)
        {
            return false;
        }

        Header header;
        try
        {
            header = DeserializeHeader(packet);
        }
        catch
        {
            // Let the stock path (and its logging) handle anything malformed.
            return false;
        }

        if (header.PacketType != PacketType.N3Message)
        {
            return false;
        }

        // The N3 message type sits immediately after the 16-byte header.
        int n3MessageType;
        using (var peekStream = new MemoryStream(packet))
        using (var peekReader = new MessagingStreamReader(peekStream))
        {
            peekReader.Position = 16;
            n3MessageType = peekReader.ReadInt32();
        }

        if (n3MessageType != (int)N3MessageType.SimpleCharFullUpdate)
        {
            return false;
        }

        // Confirmed SimpleCharFullUpdate. Parse it with the corrected reader.
        // Any failure here propagates to ProcessCachedPacket's catch and is
        // logged once, exactly as a stock deserialize failure would be.
        using (var bodyStream = new MemoryStream(packet))
        using (var bodyReader = new MessagingStreamReader(bodyStream))
        {
            bodyReader.Position = 16;
            var body = SimpleCharFullUpdateReader.Read(bodyReader);
            aoMessage = new AOMessage { Header = header, Body = body, };
        }

        return true;
    }

    /// <summary>
    ///     FullCharacter handling. Returns false for non-FullCharacter packets (caller uses the stock path).
    ///     For a FullCharacter it PREFERS the stock serializer (unchanged for the common no-pet case) and only
    ///     falls back to <see cref="FullCharacterReader" /> when the stock serializer throws — which it does
    ///     whenever the character has a pet up (the trailing pet identity is mis-read as a TeamMember struct
    ///     and it reads past the end). The fallback keeps our stats + spell list and extracts the pet list.
    /// </summary>
    private bool TryDeserializeFullCharacter(byte[] packet, out AOMessage aoMessage)
    {
        aoMessage = null;
        if (!IsN3MessageType(packet, N3MessageType.FullCharacter))
        {
            return false;
        }

        try
        {
            aoMessage = _serializer.Deserialize(packet);
            return true;
        }
        catch
        {
            var header = DeserializeHeader(packet);
            using (var bodyStream = new MemoryStream(packet))
            using (var bodyReader = new MessagingStreamReader(bodyStream))
            {
                bodyReader.Position = 16;
                var body = FullCharacterReader.Read(bodyReader, packet.Length);
                aoMessage = new AOMessage { Header = header, Body = body, };
            }

            return true;
        }
    }

    /// <summary>
    ///     Extract the nano a SpellList message uploaded and add it to the LOCAL player's SpellList live.
    ///     The SpellList body has a variable effect block we can't cheaply skip, but the uploaded nano lives
    ///     in a fixed 14-byte tail: [HasNano=1][Nano.Type(4)][Nano.Instance(4)][UnreadFlag][ApplyScope(4)].
    ///     So we read the tail, not the effects. A stray misread is harmless — a bogus id just fails the nano
    ///     lookup in AutoSummons and is ignored. Only applies to our own character (identity at offset 24).
    /// </summary>
    private void TryApplySpellListNano(byte[] packet)
    {
        try
        {
            if (packet == null || packet.Length < 38)
            {
                return; // 16 header + type + identity + ≥14 tail
            }

            using (var ms = new MemoryStream(packet))
            using (var r = new MessagingStreamReader(ms))
            {
                r.Position = 24; // character Identity.Instance
                var charInstance = r.ReadInt32();
                if (Client.LocalDynelId != 0 && charInstance != Client.LocalDynelId)
                {
                    return;
                }

                r.Position = packet.Length - 14; // start of the [HasNano][Nano][…] tail
                if (r.ReadByte() != 1)
                {
                    return; // HasNano == 0: nothing uploaded here
                }

                r.ReadInt32(); // Nano.Type
                var nanoId = r.ReadInt32(); // Nano.Instance == the nano id
                if (nanoId <= 0)
                {
                    return;
                }

                var me = DynelManager.LocalPlayer;
                if (me != null)
                {
                    me.AddUploadedNano(nanoId);
                }
            }
        }
        catch
        {
            /* never let a malformed SpellList disrupt processing */
        }
    }

    /// <summary>
    ///     True if <paramref name="packet" /> is an N3 message of the given type. Peeks the 4-byte N3
    ///     type that sits right after the 16-byte header without deserializing the (possibly broken) body.
    /// </summary>
    private bool IsN3MessageType(byte[] packet, N3MessageType type)
    {
        if (packet == null || packet.Length < 20)
        {
            return false;
        }

        Header header;
        try
        {
            header = DeserializeHeader(packet);
        }
        catch
        {
            return false;
        }

        if (header.PacketType != PacketType.N3Message)
        {
            return false;
        }

        using (var peekStream = new MemoryStream(packet))
        using (var peekReader = new MessagingStreamReader(peekStream))
        {
            peekReader.Position = 16;
            return peekReader.ReadInt32() == (int)type;
        }
    }

    private Header DeserializeHeader(byte[] packet)
    {
        var headerBytes = new byte[16];
        Array.Copy(packet, 0, headerBytes, 0, 16);

        using (var headerStream = new MemoryStream(headerBytes))
        using (var headerReader = new MessagingStreamReader(headerStream))
        {
            return (Header)_headerSerializer.Deserialize(headerReader, null);
        }
    }

    // The next reconnect's delay, when a specific one is warranted: AlreadyLoggedIn means the
    // server still holds the character session - retry SHORT (it releases within seconds of the
    // old socket dying), not on the 30 s default. Consumed by the next Reconnect().
    private int _nextReconnectDelayMs;

    private void Reconnect()
    {
        try
        {
            _tcpClient?.Close();
        }
        catch (Exception ex)
        {
            _logger.Debug($"reconnect: closing the old socket failed: {ex.Message}");
        }

        _tcpClient = null;
        _sessionCookie = null;
        lock (_sendLock)
        {
            _messageId = 1;
        }

        if (Client.Config.AutoReconnect)
        {
            // GUARDED: this continuation is the only thing between a dead session and a dead bot -
            // the plain ContinueWith died silently on its first exception and the bot sat
            // disconnected until a manual restart (2026-10-08 18:53, AlreadyLoggedIn).
            var delay = _nextReconnectDelayMs > 0 ? _nextReconnectDelayMs : Client.Config.ReconnectDelay;
            _nextReconnectDelayMs = 0;
            _logger.Debug($"reconnecting in {delay} ms...");
            Task.Delay(delay).ContinueWith(t =>
            {
                try
                {
                    Connect();
                }
                catch (Exception ex)
                {
                    _logger.Error($"reconnect attempt failed: {ex.Message} - scheduling a retry.");
                    Reconnect();
                }
            });
        }
        else
        {
            _stateMachine.Fire(Trigger.Stop);
        }
    }

    private void InitializeStateMachine()
    {
        _stateMachine = new NetworkStateMachine(_logger);

        _stateMachine.OnTransitioned(OnNetworkStateTransition);

        _stateMachine.Configure(State.Idle)
            .Permit(Trigger.Connect, State.Connecting);

        _stateMachine.Configure(State.Disconnected)
            .OnEntry(() => Reconnect())
            .Ignore(Trigger.Disconnect)
            .Permit(Trigger.Connect, State.Connecting)
            .PermitReentry(Trigger.FailedToRetreiveDimensionInfo);

        _stateMachine.Configure(State.Connecting)
            .OnEntryFrom(_stateMachine.ConnectTrigger, endpoint => Connect(endpoint))
            .OnEntryFrom(_stateMachine.ConnectErrorTrigger, (endpoint, exception) => Connect(endpoint))
            .Permit(Trigger.OnTcpConnectError, State.Disconnected)
            .Permit(Trigger.Disconnect, State.Disconnected)
            .Permit(Trigger.OnTcpConnected, State.Connected);

        _stateMachine.Configure(State.Connected)
            .OnEntryFrom(Trigger.OnTcpConnected, () =>
            {
                _tcpClient.BeginReceiving();
                _stateMachine.Fire(Trigger.ConnectionEstablished);
            })
            .PermitIf(Trigger.ConnectionEstablished, State.Authenticating, () => _sessionCookie == null)
            .PermitIf(Trigger.ConnectionEstablished, State.Zoning, () => !(_sessionCookie == null))
            .Permit(Trigger.OnTcpConnectionError, State.Disconnected)
            .Permit(Trigger.Connect, State.Connecting)
            .Permit(Trigger.Disconnect, State.Disconnected);

        _stateMachine.Configure(State.Authenticating)
            .SubstateOf(State.Connected)
            .OnEntry(() =>
            {
                Send(new UserLoginMessage
                {
                    UserName = Client.Credentials.Username,
                    ClientVersion = Client.Dimension == Dimension.RubiKa ? DimensionInfo.RubiKa.Version : DimensionInfo.RubiKa2019.Version,
                });
            })
            .Permit(Trigger.Disconnect, State.Disconnected)
            .Permit(Trigger.FailedToLogin, State.Disconnected);

        _stateMachine.Configure(State.Zoning)
            .OnEntry(() => { Client.OnTeleportStart(); })
            .SubstateOf(State.Connected)
            .Permit(Trigger.CharInPlay, State.InPlay)
            .Permit(Trigger.Disconnect, State.Disconnected);

        _stateMachine.Configure(State.InPlay)
            .SubstateOf(State.Connected)
            .Ignore(Trigger.CharInPlay)
            .Permit(Trigger.Connect, State.Connecting)
            .Permit(Trigger.Disconnect, State.Disconnected);
    }

    private void Pong(AOMessage pingMsg)
    {
        var pingBody = (PingMessage)pingMsg.Body;

        var pongMsg = new AOMessage
        {
            Body = new PingMessage
            {
                PingMessageType = PingMessageType.Pong,
                ServerTime = pingBody.ServerTime,
                UpTime1 = pingBody.UpTime1,
                UpTime2 = pingBody.UpTime2,
                Unk2 = pingBody.Unk2,
            },
            Header = new Header
            {
                PacketType = PacketType.PingMessage,
                Sender = Client.LocalDynelId,
                Receiver = pingMsg.Header.Sender,
            },
        };

        Send(pongMsg);
    }

    private void OnInitiateCompressionMessage()
    {
        Send(new ZoneLoginMessage
        {
            CharacterId = Client.LocalDynelId,
            Cookie1 = _sessionCookie.Cookie1,
            Cookie2 = _sessionCookie.Cookie2,
        });
    }

    private void RegisterInternalSystemMessageHandlers()
    {
        _internalSysMsgCallbacks = new Dictionary<SystemMessageType, Action<SystemMessage>>();

        _internalSysMsgCallbacks.Add(SystemMessageType.ZoneInfo, msg =>
        {
            var zoneInfoMsg = (ZoneInfoMessage)msg;

            _sessionCookie = new SessionCookie
            {
                Cookie1 = zoneInfoMsg.Cookie1,
                Cookie2 = zoneInfoMsg.Cookie2,
            };

            _stateMachine.Fire(_stateMachine.ConnectTrigger, new IPEndPoint(zoneInfoMsg.ServerIpAddress, zoneInfoMsg.ServerPort));
        });

        _internalSysMsgCallbacks.Add(SystemMessageType.ZoneRedirection, msg =>
        {
            var zoneRedMsg = (ZoneRedirectionMessage)msg;

            _logger.Debug($"ZoneRediction to {zoneRedMsg.ServerIpAddress}:{zoneRedMsg.ServerPort}");

            _stateMachine.Fire(_stateMachine.ConnectTrigger, new IPEndPoint(zoneRedMsg.ServerIpAddress, zoneRedMsg.ServerPort));
        });

        _internalSysMsgCallbacks.Add(SystemMessageType.LoginError, msg =>
        {
            var loginErrorMsg = (LoginErrorMessage)msg;

            // AlreadyLoggedIn: the server still holds the character session of the connection that
            // just died - it releases within seconds of the old socket closing. Retry SHORT (2 s)
            // instead of the 30 s default: the bot sat 5 minutes dead on exactly this (2026-10-08
            // 18:53, the retry chain had died silently on top of it).
            if (loginErrorMsg.Error == LoginError.AlreadyLoggedIn)
            {
                _logger.Information("Failed to login: AlreadyLoggedIn - the server still holds the session, retrying in 2 s.");
                _nextReconnectDelayMs = 2000;
            }
            else
            {
                _logger.Debug($"Failed to login: {loginErrorMsg.Error}");
            }

            _stateMachine.Fire(Trigger.FailedToLogin);
        });
    }

    private void RegisterInternalN3MessageHandlers()
    {
        _internalN3MsgCallbacks = new Dictionary<N3MessageType, Action<N3Message>>();

        _internalN3MsgCallbacks.Add(N3MessageType.FullCharacter, msg => { _stateMachine.Fire(Trigger.CharInPlay); });
    }

    private void OnNetworkStateTransition(StateMachine<State, Trigger>.Transition transition)
    {
        NetworkStateChanged?.Invoke(transition);
    }
}