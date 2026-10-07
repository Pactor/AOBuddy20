// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: SystemFeedback.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Network;
using Microsoft.Extensions.Logging;
using SmokeLounge.AOtomation.Messaging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.PacketConsumers;

/// <summary>
///     SYSTEM LINES: the server's one-line feedback, logged ALWAYS - not only while some run is
///     active. The FormatFeedback ext string is formatted in managed code (ExtMessageFormatter +
///     the mmdb embedded in MmdbData.cs); the Feedback pair (category/id) is looked up in the
///     same table. Controllers keep their own handlers for state and their run narratives; this
///     is the always-on record the owner greps.
/// </summary>
public sealed class SystemFeedback
{
    private readonly ILogger<SystemFeedback> _logger;

    public SystemFeedback(ILogger<SystemFeedback> logger)
    {
        _logger = logger;
    }

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(FormatFeedbackHandler, N3MessageType.FormatFeedback, 0);
        router.Register(FeedbackHandler, N3MessageType.Feedback, 0);
    }

    private bool FormatFeedbackHandler(AOMessage arg)
    {
        if (arg.Body is FormatFeedbackMessage ff && !string.IsNullOrEmpty(ff.FormattedMessage))
        {
            _logger.LogInformation($"SYS: {ff.FormattedMessage} {ff.ChatCategory}/{ff.PayloadKind}");
        }

        return false;
    }

    private bool FeedbackHandler(AOMessage arg)
    {
        if (arg.Body is FeedbackMessage fb)
        {
            var text = MmdbData.TryGet(fb.CategoryId, fb.MessageId, out var template) ? template : $"{fb.CategoryId}/{fb.MessageId}";
            _logger.LogInformation($"SYS: {text}");
        }

        return false;
    }
}
