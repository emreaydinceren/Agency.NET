namespace Agency.Harness.Test;

/// <summary>
/// Unit tests for <see cref="InMemoryConversationManager"/>.
/// </summary>
public sealed class InMemoryConversationManagerTests
{
    [Fact]
    public void Messages_IsEmpty_WhenNewlyCreated()
    {
        IConversationManager manager = new InMemoryConversationManager();

        Assert.Empty(manager.Messages);
    }

    [Fact]
    public void Append_AddsMessage_ToMessages()
    {
        IConversationManager manager = new InMemoryConversationManager();
        var msg = new ChatMessage(ChatRole.User, [new TextContent("Hello")]);

        manager.Append(msg);

        Assert.Single(manager.Messages);
        Assert.Same(msg, manager.Messages[0]);
    }

    [Fact]
    public void Append_PreservesOrder_AcrossMultipleMessages()
    {
        IConversationManager manager = new InMemoryConversationManager();
        var user = new ChatMessage(ChatRole.User, [new TextContent("User prompt")]);
        var assistant = new ChatMessage(ChatRole.Assistant, [new TextContent("Assistant reply")]);
        var result = new ChatMessage(ChatRole.User, [new TextContent("Tool result")]);

        manager.Append(user);
        manager.Append(assistant);
        manager.Append(result);

        Assert.Equal(3, manager.Messages.Count);
        Assert.Equal(ChatRole.User, manager.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, manager.Messages[1].Role);
        Assert.Equal(ChatRole.User, manager.Messages[2].Role);
    }

    [Fact]
    public void Messages_ReturnsReadOnlyView()
    {
        IConversationManager manager = new InMemoryConversationManager();
        var messages = manager.Messages;

        // Adding via Append should be reflected in the existing reference.
        manager.Append(new ChatMessage(ChatRole.User, [new TextContent("late message")]));

        Assert.Single(messages);
    }
}
