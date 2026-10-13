using Microsoft.EntityFrameworkCore;

namespace TaskApi;

public class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();
        task.Property(x => x.Title).HasMaxLength(200).IsRequired();
        task.Property(x => x.Priority).HasConversion<string>().HasMaxLength(10);
        task.Property(x => x.RowVersion).IsRowVersion(); // PostgreSQL xmin.
    }
}
