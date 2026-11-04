namespace Solvelt.Api.Orders;

/// <summary>SQL used by the <c>OutboxScopeUnique</c> migration (kept here so a test can run exactly what the migration runs). Portable: the same text runs on SQLite and PostgreSQL.</summary>
public static class OutboxScopeSql
{
    /// <summary>
    /// A unique index over (SequenceScope, OrderKey) cannot be created while two events of one scope share a number (writers were serialized, but nothing enforced it). Renumbers every scoped
    /// event 1..n in its existing order (key, then creation time, then id), so the order of delivery is kept and the numbers are unique. Unscoped (listing) events keep their versions.
    /// </summary>
    public const string RenumberScopes = """
        UPDATE outbox_messages SET "OrderKey" = r.n
        FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "SequenceScope" ORDER BY "OrderKey", "CreatedAt", "Id") AS n
              FROM outbox_messages WHERE "SequenceScope" IS NOT NULL) r
        WHERE outbox_messages."Id" = r."Id"
        """;
}
