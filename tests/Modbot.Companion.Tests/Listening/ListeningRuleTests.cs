using Modbot.Companion.Listening;
using Modbot.Companion.Pipeline;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// When the client listens: only with the switch on, only with the model on the disk, and only
/// while VRChat is running.
/// </summary>
/// <remarks>
/// The last of those is the one that matters most. A microphone that were open whenever the client
/// was in the tray would be open all day; open only while a moderator is inside VRChat, it is open
/// while they are doing the thing it exists to help with, and closed the rest of the time.
/// </remarks>
public class ListeningRuleTests
{
    private static ListeningState Decide(
        bool on,
        LogHealthStatus log = LogHealthStatus.Healthy,
        bool supported = true,
        bool modelReady = true,
        bool getting = false,
        bool? microphoneOpen = true)
        => ListeningRule.Decide(new ListeningSettings(on), log, supported, modelReady, getting, microphoneOpen);

    [Fact]
    public void OffUntilSomebodyTurnsItOn()
    {
        Assert.False(ListeningSettings.Default.On);
        Assert.Equal(ListeningState.Off, Decide(on: false));

        // Off wins over everything else, including a machine that could not listen anyway: a
        // moderator who turned it off chose that, and the card has to be able to say so.
        Assert.Equal(ListeningState.Off, Decide(on: false, supported: false, modelReady: false));
        Assert.False(ListeningRule.ShouldListen(ListeningState.Off));
    }

    [Fact]
    public void OnlyWhileVRChatIsRunning()
    {
        Assert.Equal(ListeningState.Listening, Decide(on: true, log: LogHealthStatus.Healthy));
        Assert.Equal(ListeningState.Waiting, Decide(on: true, log: LogHealthStatus.Idle));

        // And "waiting" means no microphone is open. This is the whole of the rule that keeps the
        // client from listening all day.
        Assert.False(ListeningRule.ShouldListen(ListeningState.Waiting));
        Assert.True(ListeningRule.ShouldListen(ListeningState.Listening));
    }

    [Fact]
    public void ALogTheClientHasStoppedUnderstandingStillCountsAsVRChatRunning()
    {
        // Lines are arriving, so the moderator is in a world. Somebody whose client needs updating
        // should not also quietly lose the thing they switched on.
        Assert.Equal(ListeningState.Listening, Decide(on: true, log: LogHealthStatus.NotUnderstood));
    }

    [Fact]
    public void AMachineThatCannotListenSaysSoRatherThanReadingAsOff()
    {
        Assert.Equal(ListeningState.NotOnThisMachine, Decide(on: true, supported: false));
        Assert.False(ListeningRule.ShouldListen(ListeningState.NotOnThisMachine));
    }

    [Fact]
    public void NothingIsOpenedWhileTheModelIsStillComingOrDidNotCome()
    {
        Assert.Equal(ListeningState.Getting, Decide(on: true, modelReady: false, getting: true));
        Assert.Equal(ListeningState.NoModel, Decide(on: true, modelReady: false));

        Assert.False(ListeningRule.ShouldListen(ListeningState.Getting));
        Assert.False(ListeningRule.ShouldListen(ListeningState.NoModel));
    }

    [Fact]
    public void AMicrophoneThatIsNotOpenYetKeepsTheListenerUp()
    {
        // The listener is what opens the microphone, so taking it down for not having got one
        // would build it and tear it down once a second for as long as another program held it.
        Assert.Equal(ListeningState.NoMicrophone, Decide(on: true, microphoneOpen: false));
        Assert.True(ListeningRule.ShouldListen(ListeningState.NoMicrophone));

        // Null is "there is nothing to ask yet", which is the moment one is about to be opened.
        Assert.Equal(ListeningState.Listening, Decide(on: true, microphoneOpen: null));
    }

    [Fact]
    public void TheCardOnlyEverSaysItIsListeningWhenTheMicrophoneIsActuallyOpen()
    {
        var listening = ListeningStatus.None with { State = ListeningState.Listening };
        Assert.True(listening.IsListening);

        foreach (var state in new[]
                 {
                     ListeningState.Off, ListeningState.Waiting, ListeningState.Getting,
                     ListeningState.NoModel, ListeningState.NoMicrophone, ListeningState.NotOnThisMachine,
                 })
        {
            Assert.False((ListeningStatus.None with { State = state }).IsListening);
        }
    }

    [Fact]
    public void AMachineThatCannotListenGetsASentenceAndOneThatCanGetsNone()
    {
        Assert.Null(ListeningStatus.None.Unsupported);
        Assert.NotNull((ListeningStatus.None with { Supported = false }).Unsupported);
    }

    [Fact]
    public void TheCardOnlySaysItIsWaitingWhenAnInstructionWouldActuallyBeTaken()
    {
        var waiting = ListeningStatus.None with
        {
            State = ListeningState.Listening,
            HeardItsName = true,
        };

        Assert.True(waiting.IsWaitingForCommand);

        // Heard its name, then VRChat closed and the microphone with it. Nothing would be acted on
        // now, so nothing may say it would be.
        foreach (var state in new[]
                 {
                     ListeningState.Off, ListeningState.Waiting, ListeningState.Getting,
                     ListeningState.NoModel, ListeningState.NoMicrophone, ListeningState.NotOnThisMachine,
                 })
        {
            Assert.False((waiting with { State = state }).IsWaitingForCommand);
        }

        // And listening is not waiting: the name has to be said first.
        Assert.False((waiting with { HeardItsName = false }).IsWaitingForCommand);
    }

    [Fact]
    public void TheCardStartsOutListeningForTheClientsOwnName()
    {
        Assert.Equal("Modbot", ListeningStatus.None.Called);
        Assert.False(ListeningStatus.None.HeardItsName);
    }
}
