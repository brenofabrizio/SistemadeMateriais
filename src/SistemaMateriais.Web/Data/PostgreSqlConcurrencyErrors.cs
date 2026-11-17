using Npgsql;

namespace SistemaMateriais.Web.Data;

public static class PostgreSqlConcurrencyErrors
{
    public static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsUniqueConstraintViolation(Exception exception, string constraintName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
