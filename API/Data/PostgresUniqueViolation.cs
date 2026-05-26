using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Platform.Api.Data;

public static class PostgresUniqueViolation
{
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };
}
