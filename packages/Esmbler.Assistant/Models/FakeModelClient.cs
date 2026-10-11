namespace Esmbler.Assistant.Models;

/// <summary>
/// A model that replays scripted responses (tests and offline evaluation of the loop, ADR 0024). Each request is recorded
/// so tests can check what the loop sent.
/// </summary>
public sealed class FakeModelClient(params ModelResponse[] responses) : IModelClient
{
    private readonly Queue<ModelResponse> _responses = new(responses);

    public List<ModelRequest> Requests { get; } = [];

    public string Provider => "fake";

    public string Model => "fake-model";

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request with { Items = [.. request.Items] });
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new ModelResponse(StopKind.EndTurn, [new AssistantText("Done.")], ModelUsage.None(Model)));
    }

    public static ModelResponse Calls(params (string Name, string Arguments)[] calls) =>
        new(StopKind.ToolUse, [.. calls.Select((c, i) => (ModelItem)new ToolCall($"call_{Guid.NewGuid():N}"[..13] + i, c.Name, c.Arguments))], new ModelUsage(100, 20, 0, "fake-model"));

    public static ModelResponse Says(string text) => new(StopKind.EndTurn, [new AssistantText(text)], new ModelUsage(100, 20, 0, "fake-model"));
}
