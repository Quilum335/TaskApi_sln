using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskApi;
using Xunit;

namespace TaskApi.Tests;

public class CompleteTaskIntegrationTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };

    [Fact]
    public async Task CreateAndComplete_PersistsTaskAndPublishesEvent()
    {
        using var app = new TaskApiFactory();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new { title = "Buy milk", priority = "High" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<TaskItem>(JsonOptions))!;
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Empty(app.Publisher.Messages);

        var completion = await client.PutAsync($"/tasks/{created.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var completed = (await completion.Content.ReadFromJsonAsync<TaskItem>(JsonOptions))!;
        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.True(completed.CompletedAt >= created.CreatedAt);

        var tasks = await client.GetFromJsonAsync<List<TaskItem>>("/tasks", JsonOptions);
        var stored = Assert.Single(tasks!);
        Assert.True(stored.IsCompleted);
        Assert.Equal(completed.CompletedAt, stored.CompletedAt);
        var message = Assert.Single(app.Publisher.Messages);
        Assert.Equal(new TaskCompleted(created.Id, "Buy milk", completed.CompletedAt.Value, Priority.High), message);

        var repeated = await client.PutAsync($"/tasks/{created.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Single(app.Publisher.Messages);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/tasks/{created.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<TaskItem>>("/tasks", JsonOptions))!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task InvalidTitle_ReturnsBadRequest(string? title)
    {
        using var app = new TaskApiFactory();
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/tasks", new { title });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Publisher.Messages);
    }

    [Fact]
    public async Task TitleLengthAndDefaultPriority_AreValidated()
    {
        using var app = new TaskApiFactory();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/tasks", new { title = new string('a', 201) })).StatusCode);

        var response = await client.PostAsJsonAsync("/tasks", new { title = new string('a', 200) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = (await response.Content.ReadFromJsonAsync<TaskItem>(JsonOptions))!;
        Assert.Equal(Priority.Medium, task.Priority);
    }

    [Fact]
    public async Task InvalidPriorityOrId_ReturnsBadRequest_AndMissingTaskReturnsNotFound()
    {
        using var app = new TaskApiFactory();
        using var client = app.CreateClient();
        foreach (var priority in new object[] { "Urgent", 99 })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync("/tasks", new { title = "Task", priority })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/tasks/wrong-id/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/tasks/wrong-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/tasks/{Guid.NewGuid()}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/tasks/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentCompletion_ReturnsOneConflictAndPublishesOnce()
    {
        using var app = new TaskApiFactory();
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/tasks", new { title = "Concurrent task" });
        var task = (await response.Content.ReadFromJsonAsync<TaskItem>(JsonOptions))!;

        app.Barrier.Enabled = true;
        var completions = await Task.WhenAll(
            client.PutAsync($"/tasks/{task.Id}/complete", null),
            client.PutAsync($"/tasks/{task.Id}/complete", null));

        Assert.Single(completions, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(completions, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(app.Publisher.Messages);
    }
}
