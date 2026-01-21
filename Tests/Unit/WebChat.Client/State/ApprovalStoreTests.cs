using Domain.DTOs;
using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Approval;

namespace Tests.Unit.WebChat.Client.State;

public class ApprovalStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ApprovalStore _store;

    public ApprovalStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new ApprovalStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    private static ToolApprovalRequestMessage CreateRequest(string approvalId = "approval-1") =>
        new(approvalId, [new ToolApprovalRequest("Search", new Dictionary<string, object?> { ["query"] = "test" })]);

    [Fact]
    public void Initial_StateHasNoRequest()
    {
        _store.State.CurrentRequest.ShouldBeNull();
        _store.State.TopicId.ShouldBeNull();
        _store.State.IsResponding.ShouldBeFalse();
    }

    [Fact]
    public void ShowApproval_SetsCurrentRequest()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        _store.State.CurrentRequest.ShouldBe(request);
    }

    [Fact]
    public void ShowApproval_SetsTopicId()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        _store.State.TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void ShowApproval_SetsIsRespondingToFalse()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        _store.State.IsResponding.ShouldBeFalse();
    }

    [Fact]
    public void ApprovalResponding_SetsIsRespondingToTrue()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResponding());

        _store.State.IsResponding.ShouldBeTrue();
    }

    [Fact]
    public void ApprovalResponding_PreservesOtherState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResponding());

        _store.State.CurrentRequest.ShouldBe(request);
        _store.State.TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void ApprovalResolved_ClearsAllState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResponding());
        _dispatcher.Dispatch(new ApprovalResolved("approval-1", "tool output"));

        _store.State.CurrentRequest.ShouldBeNull();
        _store.State.TopicId.ShouldBeNull();
        _store.State.IsResponding.ShouldBeFalse();
    }

    [Fact]
    public void ApprovalResolved_ReturnsToInitialState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResolved("approval-1", null));

        _store.State.ShouldBe(ApprovalState.Initial);
    }

    [Fact]
    public void ClearApproval_ClearsAllState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ClearApproval());

        _store.State.CurrentRequest.ShouldBeNull();
        _store.State.TopicId.ShouldBeNull();
        _store.State.IsResponding.ShouldBeFalse();
    }

    [Fact]
    public void ClearApproval_ReturnsToInitialState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ClearApproval());

        _store.State.ShouldBe(ApprovalState.Initial);
    }

    [Fact]
    public void StateObservable_EmitsOnDispatch()
    {
        var emissions = new List<ApprovalState>();
        using var subscription = _store.StateObservable.Subscribe(emissions.Add);

        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        emissions.Count.ShouldBe(2); // Initial + ShowApproval
        emissions[1].CurrentRequest.ShouldBe(request);
    }

    [Fact]
    public void StateObservable_EmitsCurrentStateOnSubscription()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        ApprovalState? receivedState = null;
        using var subscription = _store.StateObservable.Subscribe(state => receivedState = state);

        receivedState.ShouldNotBeNull();
        receivedState.CurrentRequest.ShouldBe(request);
    }
}