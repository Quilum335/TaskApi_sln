using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskApi;

namespace TaskApi.Tests;

public sealed class TaskApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tasks-{Guid.NewGuid()}.db");
    public RecordingPublisher Publisher { get; } = new();
    public CompletionBarrier Barrier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TaskDbContext>();
            services.AddScoped<TaskDbContext>(_ => new TestDbContext(
                new DbContextOptionsBuilder<TaskDbContext>()
                    .UseSqlite($"Data Source={_databasePath}")
                    .AddInterceptors(Barrier)
                    .Options));
            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher>(Publisher);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_databasePath);
            File.Delete(_databasePath + "-wal");
            File.Delete(_databasePath + "-shm");
        }
    }

    private sealed class TestDbContext(DbContextOptions<TaskDbContext> options) : TaskDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // SQLite has no xmin; keep the same concurrency check.
            modelBuilder.Entity<TaskItem>().Property(x => x.RowVersion).ValueGeneratedNever();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var entry in ChangeTracker.Entries<TaskItem>().Where(x => x.State == EntityState.Modified))
                entry.Entity.RowVersion++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed class RecordingPublisher : ITaskEventPublisher
{
    public ConcurrentQueue<TaskCompleted> Messages { get; } = new();
    public void Publish(TaskCompleted message) => Messages.Enqueue(message);
}

public sealed class CompletionBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;
    public bool Enabled { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Enabled && eventData.Context!.ChangeTracker.Entries<TaskItem>().Any(x => x.State == EntityState.Modified))
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}
