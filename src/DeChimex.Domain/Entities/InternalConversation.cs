using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A one-to-one internal (work) conversation between two staff/developer accounts.
/// Pairing rules (who may talk to whom) live in DeChimex.Shared.Constants.InternalMessaging
/// and are enforced by the application layer before a conversation is created or read;
/// the aggregate itself only guarantees the two participants are distinct accounts.
/// Completely separate from customer/order conversations (OrderMessage).
/// </summary>
public sealed class InternalConversation : AggregateRoot
{
    private InternalConversation() { } // EF

    private InternalConversation(Guid initiatorId, Guid otherId)
    {
        ParticipantOneId = initiatorId;
        ParticipantTwoId = otherId;
    }

    private readonly List<InternalMessage> _messages = new();

    public Guid ParticipantOneId { get; private set; }
    public Guid ParticipantTwoId { get; private set; }

    public IReadOnlyCollection<InternalMessage> Messages => _messages.AsReadOnly();

    public static InternalConversation Start(Guid initiatorId, Guid otherId)
    {
        if (initiatorId == otherId)
            throw new InvalidOperationException("A conversation needs two distinct participants.");
        return new InternalConversation(initiatorId, otherId);
    }

    public bool Includes(Guid userId) => ParticipantOneId == userId || ParticipantTwoId == userId;

    /// <summary>The participant that is not <paramref name="userId"/> (caller verified Includes).</summary>
    public Guid OtherThan(Guid userId) => ParticipantOneId == userId ? ParticipantTwoId : ParticipantOneId;
}
