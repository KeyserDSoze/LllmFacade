using System.Collections.Concurrent;
using LllmFacade.Domain;

namespace LllmFacade.Infrastructure.Memory;

/// <summary>Development-only adapter. Data disappears when the API restarts.</summary>
public sealed class InMemoryProjectStore
{
    private readonly ConcurrentDictionary<Guid, ChatProject> _projects = new();

    public IReadOnlyList<ChatProject> List() =>
        _projects.Values.OrderBy(x => x.CreatedAt).ToArray();

    public ChatProject Create(string name)
    {
        var project = new ChatProject(Guid.NewGuid(), name, DateTimeOffset.UtcNow);
        _projects[project.Id] = project;
        return project;
    }
}

