using System.Text.Json.Serialization;

namespace TaskApi;

public enum Priority { Low, Medium, High }

public class TaskItem
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;

    [JsonIgnore]
    public uint RowVersion { get; set; }
}

public record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);

public record TaskCompleted(Guid TaskId, string Title, DateTimeOffset CompletedAt, Priority Priority);
