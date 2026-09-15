namespace Modbot.Core.Data.Entities;

/// <summary>
/// One conversation on the Chat page, owned by one Modbot account. The table is
/// <c>ai_chat_conversation</c>.
/// </summary>
/// <remarks>
/// A person's own scratch pad, not moderation history: it is visible only to its owner, is not a
/// fact, and goes when the account does (AI chat design §6).
/// </remarks>
public class AiChatConversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public ModbotUser User { get; set; } = null!;

    /// <summary>The start of the first message, so the list has something to show.</summary>
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the last message was added. The list is newest first by this.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public List<AiChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// One message in a conversation: what the person asked, a tool call and its result, or the
/// model's reply. The table is <c>ai_chat_message</c>.
/// </summary>
public class AiChatMessage
{
    /// <summary>Increasing, so it is also the order the messages were added in.</summary>
    public long Id { get; set; }

    public Guid ConversationId { get; set; }

    public AiChatConversation Conversation { get; set; } = null!;

    /// <summary><c>user</c>, <c>assistant</c> or <c>tool</c>.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>The text: the question, the reply, or the tool result sent back to the model.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>For a reply that asked for tools: a JSON array of <c>{id, name, arguments}</c>.</summary>
    public string? ToolCalls { get; set; }

    /// <summary>For a tool result: which call it answers.</summary>
    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    /// <summary>For a tool result: a JSON array of the people, worlds and rooms it named.</summary>
    public string? Mentioned { get; set; }

    /// <summary>For a tool result: whether the tool ran and answered.</summary>
    public bool? Worked { get; set; }

    public int? DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
