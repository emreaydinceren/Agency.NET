namespace Agency.Harness.Test;

/// <summary>
/// Unit tests for <see cref="InMemoryConversationManager"/>.
/// </summary>
public sealed class InMemoryConversationManagerTests
{
    /// <summary>
    /// A freshly constructed manager starts with an empty <c>Messages</c> collection.
    /// </summary>
    [Fact]
    public void Messages_IsEmpty_WhenNewlyCreated()
    {
        InMemoryConversationManager manager = new InMemoryConversationManager();

        Assert.Empty(manager.Messages);
    }

    /// <summary>
    /// Appending a message adds the exact same instance to <c>Messages</c>.
    /// </summary>
    [Fact]
    public void Append_AddsMessage_ToMessages()
    {
        InMemoryConversationManager manager = new InMemoryConversationManager();
        var msg = new ChatMessage(ChatRole.User, [new TextContent("Hello")]);

        manager.Append(msg);

        Assert.Single(manager.Messages);
        Assert.Same(msg, manager.Messages[0]);
    }

    /// <summary>
    /// Successive appends preserve insertion order in <c>Messages</c>.
    /// </summary>
    [Fact]
    public void Append_PreservesOrder_AcrossMultipleMessages()
    {
        InMemoryConversationManager manager = new InMemoryConversationManager();
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

    /// <summary>
    /// The <c>Messages</c> collection is a live read-only view: appends made after it was
    /// retrieved are still visible through the same reference.
    /// </summary>
    [Fact]
    public void Messages_ReturnsReadOnlyView()
    {
        InMemoryConversationManager manager = new InMemoryConversationManager();
        var messages = manager.Messages;

        // Adding via Append should be reflected in the existing reference.
        manager.Append(new ChatMessage(ChatRole.User, [new TextContent("late message")]));

        Assert.Single(messages);
    }
}
