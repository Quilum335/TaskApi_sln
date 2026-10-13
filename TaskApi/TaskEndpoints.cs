using Microsoft.EntityFrameworkCore;

namespace TaskApi;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/tasks");
        tasks.MapPost("", Create);
        tasks.MapGet("", async (TaskDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Tasks.AsNoTracking().ToListAsync(ct)));
        tasks.MapPut("/{id}/complete", Complete);
        tasks.MapDelete("/{id}", Delete);
    }

    private static async Task<IResult> Create(CreateTaskRequest request, TaskDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Title must contain between 1 and 200 characters."]
            });

        if (!Enum.IsDefined(request.Priority))
            return Results.BadRequest(new { error = "Invalid priority." });

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            Priority = request.Priority
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/tasks/{task.Id}", task);
    }

    private static async Task<IResult> Complete(Guid id, TaskDbContext db, ITaskEventPublisher publisher, CancellationToken ct)
    {
        var task = await db.Tasks.FindAsync([id], ct);
        if (task is null)
            return Results.NotFound();
        if (task.IsCompleted)
            return Results.Conflict(new { error = "Task is already completed." });

        task.IsCompleted = true;
        task.CompletedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "Task was changed by another request." });
        }

        publisher.Publish(new TaskCompleted(task.Id, task.Title, task.CompletedAt.Value, task.Priority));
        return Results.Ok(task);
    }

    private static async Task<IResult> Delete(Guid id, TaskDbContext db, CancellationToken ct)
    {
        var deleted = await db.Tasks.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}
