using System.Collections.Concurrent;
using VesperaHub.Server.Models;

namespace VesperaHub.Server.Services;

public class MessageHistoryService
{
    private readonly ConcurrentQueue<ChatMessage> _messages = new();
    private const int MaxHistory = 100;

    public void AddMessage(ChatMessage message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > MaxHistory)
        {
            _messages.TryDequeue(out _);
        }
    }

    public IReadOnlyList<ChatMessage> GetRecentMessages() => _messages.ToList();
}