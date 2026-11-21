using Modbot.Companion.Dictation;

namespace Modbot.Companion.Chat;

/// <summary>
/// What a pop-up chat's text box and microphone are tied to: that chat's own conversation, and no
/// other. Typed words and dictated words both go into the one <see cref="ChatSession"/> it was made with.
/// </summary>
/// <remarks>
/// <para><strong>One microphone for all.</strong> The <see cref="DictationController"/> is the chat's
/// own, so its button shows its own state, but the recorder under it takes its turn with the others
/// (<see cref="MicrophoneTurns"/>): while another button listens, this one is told so and does not open
/// the microphone as well.</para>
/// <para><strong>Sending.</strong> The microphone's button says Sending until the first words of the
/// answer come, as Dictate's does, whoever asked.</para>
/// </remarks>
public sealed class ChatInput
{
    private readonly ChatSession _session;

    /// <param name="dictation">This chat's own microphone button behaviour.</param>
    /// <param name="session">The conversation both the text box and the microphone talk to.</param>
    public ChatInput(DictationController dictation, ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        ArgumentNullException.ThrowIfNull(session);

        Dictation = dictation;
        _session = session;

        // The words go into this conversation as soon as they are written out.
        dictation.Finished += text => _ = _session.AskAsync(text);
        session.AnswerStarted += dictation.SendEnded;
    }

    /// <summary>This chat's microphone: a tap, and what its button shows.</summary>
    public DictationController Dictation { get; }

    /// <summary>Sends typed words into this chat's conversation. Blank text sends nothing.</summary>
    public Task SendAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _session.AskAsync(text);
    }
}
