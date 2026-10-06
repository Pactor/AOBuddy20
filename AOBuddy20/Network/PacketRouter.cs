// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PacketRouter.cs
// 
// Last modified: 2026-09-30 00:19
// Created:       2026-09-29 23:09
// 
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Extensions;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages;

namespace AOBuddy20.Network;

[MinLogLevel(LogEventLevel.Debug)]
public sealed class PacketRouter
{
    private readonly Dictionary<ChatMessageType, List<ChatPacketHandlerEntry>> _chatHandlers
        = new Dictionary<ChatMessageType, List<ChatPacketHandlerEntry>>();

    private readonly ILogger<PacketRouter> _logger;

    private readonly Dictionary<N3MessageType, List<PacketHandlerEntry>> _n3Handlers
        = new Dictionary<N3MessageType, List<PacketHandlerEntry>>();

    private readonly Dictionary<SystemMessageType, List<SystemPacketHandlerEntry>> _systemHandlers
        = new Dictionary<SystemMessageType, List<SystemPacketHandlerEntry>>();

    public PacketRouter(ILogger<PacketRouter> logger)
    {
        _logger = logger;
        _logger.LogInformation("PacketRouter initialized.");
    }


    public void Init()
    {
        Client.Chat.NetworkMessageReceived += DispatchChatMessage;
        Client.MessageReceived += Dispatch;
    }

    private void DispatchChatMessage(object? sender, ChatMessage e)
    {
        if (_chatHandlers.TryGetValue(e.Header.PacketType, out var list))
        {
            foreach (var entry in list.OrderBy(x => x.canEndSequence).ThenBy(x => x.receivePriority))
            {
                var end = false;
                try
                {
                    end = entry.Handler(e);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Chat handler for {e.Header.PacketType} threw.");
                }

                if (entry.canEndSequence && end)
                {
                    break; // ← handled=true, stop propagation
                }
            }
        }
    }


    public void RegisterChatHandler(Func<ChatMessage, bool> handler, ChatMessageType type, int receivePriority, bool endSequence = false)
    {
        _chatHandlers.GetOrAdd(type, _ => new List<ChatPacketHandlerEntry>()).Add(new ChatPacketHandlerEntry(handler, endSequence, receivePriority));
    }

    public void RegisterSystemHandler(Func<SystemMessage, bool> handler, SystemMessageType type, int receivePriority, bool endSequence = false)
    {
        _systemHandlers.GetOrAdd(type, _ => new List<SystemPacketHandlerEntry>())
            .Add(new SystemPacketHandlerEntry(handler, endSequence, receivePriority));
    }

    public void Register(Func<AOMessage, bool> handler, N3MessageType type, int receivePriority, bool canEndSequence = false)
    {
        _n3Handlers.GetOrAdd(type, _ => new List<PacketHandlerEntry>()).Add(new PacketHandlerEntry(handler, canEndSequence, receivePriority));
    }


    private readonly System.Collections.Concurrent.ConcurrentDictionary<N3MessageType, int> _unhandledN3 = new();
    private readonly List<Action<N3Message>> _n3Observers = new();
    private readonly List<Action<SystemMessage>> _systemObservers = new();
    private int _rawDoor;

    /// <summary>Observe EVERY decoded N3 message, across all types - diagnostics that must see
    /// the wire rather than one path's view of it (the buff window's invite watcher). Observers
    /// never consume: whatever they do, type dispatch and the SDK's own callbacks run on.</summary>
    public void RegisterN3Observer(Action<N3Message> observer)
    {
        _n3Observers.Add(observer);
    }

    /// <summary>Observe every decoded SystemMessage - the N3 observers' counterpart for the
    /// text-channel packet space.</summary>
    public void RegisterSystemObserver(Action<SystemMessage> observer)
    {
        _systemObservers.Add(observer);
    }

    public void Dispatch(object? sender, AOMessage e)
    {
        // A consumer that throws must not take the SDK's own handling of the packet down with it:
        // NetworkSession calls MessageReceived BEFORE its own callbacks, and its catch-all would skip
        // them for this packet (movement, stats, dynels all freeze for one message). Guard every
        // handler call; a throw counts as "not handled".
        if (e.Body == null && e.RawPacket is { Length: > 20 } raw)
        {
            // A decode failure leaves the body empty and the message invisible to every typed
            // consumer - say so, with the N3 type straight from the wire (big-endian at 16).
            var t = (raw[16] << 24) | (raw[17] << 16) | (raw[18] << 8) | raw[19];
            _unhandledN3.TryAdd((N3MessageType)t, 0);
            if (_unhandledN3[(N3MessageType)t] < 3)
            {
                _unhandledN3[(N3MessageType)t]++;
                _logger.LogInformation(
                    $"ROUTER: N3 type {t} ({(N3MessageType)t}) arrived UNDECODABLE (body empty), {raw.Length} bytes: " +
                    Convert.ToHexString(raw, 0, Math.Min(32, raw.Length)));
            }
        }

        // The raw truth about door updates: counted straight off the wire, whatever the typed
        // decode does with them (owner, 2026-10-03: none were reaching the mission controller).
        if (e.RawPacket is { Length: > 20 } rw &&
            rw[16] == 0x36 && rw[17] == 0x5A && rw[18] == 0x50 && rw[19] == 0x71)
        {
            _rawDoor++;
            if (_rawDoor <= 3 || _rawDoor % 25 == 0)
            {
                _logger.LogInformation(
                    $"ROUTER: raw DoorFullUpdate #{_rawDoor}, typed body: {e.Body?.GetType().Name ?? "none"}.");
            }
        }

        if (e.Body is N3Message n3Message)
        {
            foreach (var observer in _n3Observers)
            {
                try
                {
                    observer(n3Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "N3 observer threw.");
                }
            }

            if (!_n3Handlers.ContainsKey(n3Message.N3MessageType))
            {
                _unhandledN3.TryAdd(n3Message.N3MessageType, 0);
                if (_unhandledN3[n3Message.N3MessageType] < 3)
                {
                    _unhandledN3[n3Message.N3MessageType]++;
                    _logger.LogDebug($"ROUTER: no handler for N3 type {n3Message.N3MessageType} ({n3Message.GetType().Name}).");
                }
            }
            else if (_n3Handlers.TryGetValue(n3Message.N3MessageType, out var list))
            {
                foreach (var entry in list.OrderBy(x => x.canEndSequence).ThenBy(x => x.receivePriority))
                {
                    var end = false;
                    try
                    {
                        end = entry.Handler(e);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Packet handler for {n3Message.N3MessageType} threw.");
                    }

                    if (entry.canEndSequence && end)
                    {
                        break; // ← handled=true, stop propagation
                    }
                }
            }
        }

        if (e.Body is SystemMessage system)
        {
            foreach (var observer in _systemObservers)
            {
                try
                {
                    observer(system);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "System observer threw.");
                }
            }

            if (_systemHandlers.TryGetValue(system.SystemMessageType, out var list))
            {
                foreach (var entry in list.OrderBy(x => x.canEndSequence).ThenBy(x => x.receivePriority))
                {
                    var end = false;
                    try
                    {
                        end = entry.Handler(system);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"System handler for {system.SystemMessageType} threw.");
                    }

                    if (entry.canEndSequence && end)
                    {
                        break; // ← handled=true, stop propagation
                    }
                }
            }
        }
    }

    public record PacketHandlerEntry(Func<AOMessage, bool> Handler, bool canEndSequence, int receivePriority)
    {
    }

    public record SystemPacketHandlerEntry(Func<SystemMessage, bool> Handler, bool canEndSequence, int receivePriority)
    {
    }

    public record ChatPacketHandlerEntry(Func<ChatMessage, bool> Handler, bool canEndSequence, int receivePriority)
    {
    }
}