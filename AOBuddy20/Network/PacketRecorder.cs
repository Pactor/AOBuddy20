// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PacketRecorder.cs
//
// Created:       2026-10-12
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Network;

/// <summary>
///     THE DIAGNOSTIC MODE ('diagnostic on|off'): every packet on the wire, both directions,
///     one line each into diag-packets.log next to the host exe. The file is OVERWRITTEN each
///     time the mode turns on - one file is one capture; a bot restart (the mode is not
///     persisted) or a fresh 'diagnostic on' starts clean.
///     Line shape, built for grep first and offline decode second:
///     #000042 2026-10-12 14:25:36.479 S N3/DoorFullUpdate len=64 hex=....
///     ;DOOR (ACGEntrance:C02A0227) room=1 adj=-1 pos=(1736.8,24.4,2500.9) ...
///     '#' lines are the raw wire: the full bytes of EVERY packet (the raw tap in
///     NetworkSession raises before any parsing, so the ones the stock serializer cannot
///     parse are recorded too). ';' lines are what the typed readers made of the door /
///     door-status / zone-in packets just above them.
/// </summary>
public sealed class PacketRecorder
{
    private const string FileName = "diag-packets.log";
    private readonly object _lock = new object();

    private readonly ILogger<PacketRecorder> _logger;
    private long _count; // packets captured this capture (the status reply reads it after Stop)
    private string _path = "";
    private long _seq;
    private StreamWriter? _writer;

    public PacketRecorder(ILogger<PacketRecorder> logger)
    {
        _logger = logger;
        // The raw tap fires for every packet, both ways, before any parsing (NetworkSession
        // raises it first thing per packet). Subscribed for the bot's whole life; the writer
        // gates the work, so an OFF recorder costs one null check per packet.
        Client.PacketRaw += OnPacketRaw;
    }

    /// <summary>
    ///     The decoded companions to the raw lines (the ';' lines): the mission entrance's own
    ///     dialect - doors carry their world spot, the two rooms they connect and the state bits
    ///     of stat 0; the zone-in names the playfield the following burst belongs to.
    /// </summary>
    public void RegisterPackets(PacketRouter router)
    {
        router.Register(DoorHandler, N3MessageType.DoorFullUpdate, 0);
        router.Register(DoorStatusHandler, N3MessageType.DoorStatusUpdate, 0);
        router.Register(ZoneHandler, N3MessageType.PlayfieldAnarchyF, 0);
    }

    // ── the mode ('diagnostic on|off|status') ──────────────────────────────────────────────

    public void Command(string[] p, Action<string> reply)
    {
        switch (p.Length > 1 ? p[1].ToLowerInvariant() : "status")
        {
            case "on":
                reply(Start());
                break;
            case "off":
                reply(StopRecording());
                break;
            default:
                reply(Status());
                break;
        }
    }

    private string Start()
    {
        lock (_lock)
        {
            if (_writer != null)
            {
                return $"Diagnostic is already ON ({_count} packets so far) - still writing {_path}.";
            }

            _path = Path.Combine(AppContext.BaseDirectory, FileName);
            _seq = 0;
            _count = 0;
            // OVERWRITE: one file is one capture. A capture that appends across runs would make
            // every grep answer "which run was this line from?".
            _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read));
            _writer.WriteLine($";diag-packets v1 opened={DateTime.Now:yyyy-MM-dd HH:mm:ss} (overwritten on every 'diagnostic on')");
            _logger.LogInformation("DIAG: packet capture ON -> {Path} (file overwritten).", _path);
            return $"Diagnostic ON: every packet lands in {FileName} (fresh file).";
        }
    }

    private string StopRecording()
    {
        lock (_lock)
        {
            if (_writer == null)
            {
                return "Diagnostic is OFF.";
            }

            _writer.WriteLine($";closed={DateTime.Now:yyyy-MM-dd HH:mm:ss} packets={_count}");
            _writer.Dispose();
            _writer = null;
            _logger.LogInformation("DIAG: packet capture OFF after {Count} packets -> {Path}.", _count, _path);
            return $"Diagnostic OFF after {_count} packets -> {_path}";
        }
    }

    private string Status()
    {
        lock (_lock)
        {
            return _writer == null
                ? $"Diagnostic is OFF (last file: {_path})."
                : $"Diagnostic is ON ({_count} packets so far) -> {_path}";
        }
    }

    // ── the wire ───────────────────────────────────────────────────────────────────────────

    private void OnPacketRaw(byte[] packet, bool fromServer)
    {
        if (_writer == null)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                if (_writer == null)
                {
                    return;
                }

                _writer.WriteLine(
                    $"#{_seq++:D6} {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {(fromServer ? 'S' : 'C')} " +
                    $"{Describe(packet)} len={packet.Length} hex={Convert.ToHexString(packet)}");
                _count++;
            }
        }
        catch (Exception ex)
        {
            // A dead sink must not take the wire down with it: give up on the file. (The raw
            // event's own guard would swallow every further failure silently.)
            _writer = null;
            _logger.LogError(ex, "DIAG: packet capture gave up on {Path}.", _path);
        }
    }

    /// <summary>
    ///     The packet's type name for the line: the 16-byte header is big-endian (HeaderSerializer)
    ///     - MessageId u16, PacketType i16, unknown i16, size i16, sender i32, receiver i32 - and an
    ///     N3 message carries its own type id right behind the header, the same peek PacketRouter
    ///     does on a body the serializer failed.
    /// </summary>
    private static string Describe(byte[] p)
    {
        if (p.Length < 16)
        {
            return "?";
        }

        var packetType = (PacketType)((p[2] << 8) | p[3]);
        if (packetType != PacketType.N3Message || p.Length < 20)
        {
            return packetType.ToString();
        }

        var n3 = (N3MessageType)((p[16] << 24) | (p[17] << 16) | (p[18] << 8) | p[19]);
        return $"N3/{n3}";
    }

    // ── the ';' decoded lines ──────────────────────────────────────────────────────────────

    private void Line(string text)
    {
        lock (_lock)
        {
            if (_writer == null)
            {
                return;
            }

            _writer.WriteLine(text);
        }
    }

    // The entrance dynel's own message: identity, the two rooms it connects, its world spot and
    // stat 0's state bits (0x40 locked, 0x80 open - MissionController.DoorFullUpdateHandler).
    private bool DoorHandler(AOMessage arg)
    {
        if (arg.Body is not DoorFullUpdateMessage d)
        {
            return false;
        }

        var flags = 0u;
        if (d.Stats != null)
        {
            foreach (var s in d.Stats)
            {
                if (s.Value1 == Stat.Flags)
                {
                    flags = s.Value2;
                    break;
                }
            }
        }

        Line($";DOOR {d.Owner} room={d.Room} adj={d.AdjoiningRoom} " +
             $"pos=({d.Coordinate.X:0.0},{d.Coordinate.Y:0.0},{d.Coordinate.Z:0.0}) pf={d.Playfield} " +
             $"flags=0x{flags:X}{((flags & 0x40) != 0 ? " LOCKED" : "")}{((flags & 0x80) != 0 ? " OPEN" : "")} " +
             $"lockdiff={d.LockDifficulty} name='{d.Name}'");
        return false;
    }

    private bool DoorStatusHandler(AOMessage arg)
    {
        if (arg.Body is not DoorStatusUpdateMessage s)
        {
            return false;
        }

        Line($";DOORSTATUS {s.Identity} {(s.Open != 0 ? "OPEN" : "closed")}");
        return false;
    }

    private bool ZoneHandler(AOMessage arg)
    {
        if (arg.Body is not PlayfieldAnarchyFMessage z)
        {
            return false;
        }

        Line($";ZONE {z.PlayfieldId1} " +
             $"pos=({z.CharacterCoordinates.X:0.0},{z.CharacterCoordinates.Y:0.0},{z.CharacterCoordinates.Z:0.0})");
        return false;
    }
}