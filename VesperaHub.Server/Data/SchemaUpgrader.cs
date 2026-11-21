using Microsoft.EntityFrameworkCore;

namespace VesperaHub.Server.Data;

/// <summary>
/// Creates the database if it doesn't exist, and upgrades databases created by earlier versions
/// of Vespera Hub in place (adding tables and columns) so existing accounts and messages are kept.
/// Every statement is idempotent, so this is safe to run on every startup.
/// </summary>
public static class SchemaUpgrader
{
    public static void Upgrade(VesperaDbContext db, ILogger logger)
    {
        if (db.Database.EnsureCreated())
        {
            logger.LogInformation("Created a new database.");
            return;
        }

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Attachments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Attachments" PRIMARY KEY,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Size" INTEGER NOT NULL,
                "StoredName" TEXT NOT NULL,
                "UploaderId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_Attachments_Users_UploaderId" FOREIGN KEY ("UploaderId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Reactions" (
                "MessageId" INTEGER NOT NULL,
                "UserId" INTEGER NOT NULL,
                "Emoji" TEXT NOT NULL,
                CONSTRAINT "PK_Reactions" PRIMARY KEY ("MessageId", "UserId", "Emoji"),
                CONSTRAINT "FK_Reactions_Messages_MessageId" FOREIGN KEY ("MessageId") REFERENCES "Messages" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_Reactions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            """);

        var columns = db.Database
            .SqlQueryRaw<string>("""SELECT "name" AS "Value" FROM pragma_table_info('Messages')""")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newColumns = new (string Name, string Sql)[]
        {
            ("EditedAt", """ALTER TABLE "Messages" ADD COLUMN "EditedAt" TEXT NULL"""),
            ("DeletedAt", """ALTER TABLE "Messages" ADD COLUMN "DeletedAt" TEXT NULL"""),
            ("ReplyToId", """ALTER TABLE "Messages" ADD COLUMN "ReplyToId" INTEGER NULL REFERENCES "Messages" ("Id")"""),
            ("AttachmentId", """ALTER TABLE "Messages" ADD COLUMN "AttachmentId" TEXT NULL REFERENCES "Attachments" ("Id")"""),
        };

        foreach (var (name, sql) in newColumns)
        {
            if (columns.Contains(name)) continue;
            db.Database.ExecuteSqlRaw(sql);
            logger.LogInformation("Database upgrade: added Messages.{Column}.", name);
        }

        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_Messages_ReplyToId" ON "Messages" ("ReplyToId")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_Messages_AttachmentId" ON "Messages" ("AttachmentId")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_Reactions_UserId" ON "Reactions" ("UserId")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_Attachments_UploaderId" ON "Attachments" ("UploaderId")""");
    }
}
