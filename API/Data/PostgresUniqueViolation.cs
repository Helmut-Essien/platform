using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Platform.Api.Data;

public static class PostgresUniqueViolation
{
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    /// <summary>Name of the violated index/constraint, when the database reported one.</summary>
    public static string? ConstraintName(DbUpdateException exception) =>
        (exception.InnerException as PostgresException)?.ConstraintName;
}
