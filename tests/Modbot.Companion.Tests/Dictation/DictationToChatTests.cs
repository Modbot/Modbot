using Modbot.Companion.Chat;
using Modbot.Companion.Dictation;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Dictation;

/// <summary>What Dictate finishes with reaches the chat conversation, the way the companion wires it.</summary>
public class DictationToChatTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task WhatWasSaidIsAskedAndASecondDictationWhileOpenGoesIntoTheSameConversation()
    {
        var client = new Client();
        var session = new ChatSession(client, () => Pairing);
        var recognizer = new Recognizer();
        var controller = Controller(recognizer);
        var asking = new List<Task>();
        controller.Finished += text => asking.Add(session.AskAsync(text));

        recognizer.Says = "who is here";
        await Dictate(controller);
        recognizer.Says = "and in the other instance";
        await Dictate(controller);
        await Task.WhenAll(asking);

        Assert.Equal(["who is here", "and in the other instance"], client.Texts);
        Assert.Equal([null, client.Conversation], client.ConversationsSent);
        Assert.Equal(4, session.Lines.Count);
    }

    [Fact]
    public async Task ClosingThePanelMeansTheNextDictationStartsANewConversation()
    {
        var client = new Client();
        var session = new ChatSession(client, () => Pairing);
        var recognizer = new Recognizer { Says = "who is here" };
        var controller = Controller(recognizer);
        var asking = new List<Task>();
        controller.Finished += text => asking.Add(session.AskAsync(text));

        await Dictate(controller);
        await Task.WhenAll(asking);
        session.Close();
        await Dictate(controller);
        await Task.WhenAll(asking);

        Assert.Equal([null, null], client.ConversationsSent);
        Assert.Equal(2, session.Lines.Count);
    }

    [Fact]
    public async Task AQuietRecordingAsksNothing()
    {
        var client = new Client();
        var session = new ChatSession(client, () => Pairing);
        var controller = Controller(new Recognizer { Says = "   " });
        controller.Finished += text => _ = session.AskAsync(text);

        await Dictate(controller);

        Assert.Equal(0, client.Sends);
        Assert.Empty(session.Lines);
    }

    private static DictationController Controller(Recognizer recognizer)
        => new(new Recorder(), recognizer, () => true, _ => Task.FromResult<string?>(null), (_, _) => { });

    private static async Task Dictate(DictationController controller)
    {
        controller.Toggle();
        controller.Toggle();
        await controller.WhenSettled;
    }

    private sealed class Recorder : IDictationRecorder
    {
        private static readonly float[] Speech = Enumerable.Repeat(0.3f, 16_000).ToArray();

        public event Action? LimitReached
        {
            add { }
            remove { }
        }

        public string? Start() => null;

        public float[] Stop() => Speech;
    }

    private sealed class Recognizer : IDictationRecognizer
    {
        public string Says { get; set; } = string.Empty;

        public string Recognize(float[] samples) => Says;
    }

    private sealed class Client : IChatClient
    {
        public Guid Conversation { get; } = Guid.NewGuid();

        public int Sends => Texts.Count;

        public List<string> Texts { get; } = [];

        public List<Guid?> ConversationsSent { get; } = [];

        public Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
            => Task.FromResult<bool?>(true);

        public Task<ChatSendResult> SendAsync(
            ServerPairing pairing, string text, Guid? conversationId, Action<string> onAnswer, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            ConversationsSent.Add(conversationId);
            onAnswer("An answer.");

            return Task.FromResult(new ChatSendResult(null, Conversation));
        }
    }
}
