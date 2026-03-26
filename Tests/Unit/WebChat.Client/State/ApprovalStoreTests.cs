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
        new(approvalId,
            [new ToolApprovalRequest(null, "Search", new Dictionary<string, object?> { ["query"] = "test" })]);

    [Fact]
    public void ShowApproval_SetsAllStateCorrectly()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));

        _store.State.CurrentRequest.ShouldBe(request);
        _store.State.TopicId.ShouldBe("topic-1");
        _store.State.IsResponding.ShouldBeFalse();
    }

    [Fact]
    public void ApprovalResponding_SetsIsRespondingAndPreservesState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResponding());

        _store.State.IsResponding.ShouldBeTrue();
        _store.State.CurrentRequest.ShouldBe(request);
        _store.State.TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void ApprovalResolved_ReturnsToInitialState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ApprovalResponding());
        _dispatcher.Dispatch(new ApprovalResolved("approval-1", "tool output"));

        _store.State.ShouldBe(ApprovalState.Initial);
    }

    [Fact]
    public void ClearApproval_ReturnsToInitialState()
    {
        var request = CreateRequest();
        _dispatcher.Dispatch(new ShowApproval("topic-1", request));
        _dispatcher.Dispatch(new ClearApproval());

        _store.State.ShouldBe(ApprovalState.Initial);
    }

}
