/* =====================================================================================
   Database principals — migration identity vs runtime identity

   Run this ONCE, connected as the Entra SQL administrator (the group named in
   sqlAdminGroupName), against the fctelecom database. Not as the application.

   ------------------------------------------------------------------------------------
   WHY TWO IDENTITIES

   The identity that applies migrations needs to create and alter tables. The identity the
   running application uses must not. Collapsing them means the web tier holds schema rights
   for the entire life of the system, so any SQL injection or deserialisation flaw reaches
   DROP TABLE rather than stopping at SELECT.

   This is also what makes the audit trail meaningful. dbo.AuditEntries is DENY UPDATE, DELETE
   to the application — but a principal with ALTER rights can simply drop the DENY. The
   application must not be able to.

     MIGRATION identity   an Entra group (humans, plus the deploy pipeline's service
                          principal). db_ddladmin + db_datareader + db_datawriter.
                          Used by `dotnet ef database update` and by the CD pipeline.
                          NOT configured in the application at all.

     RUNTIME identity     the App Service system-assigned managed identity. Reader and
                          writer only, with DDL explicitly denied. This is what the
                          connection string's Authentication=Active Directory Default
                          resolves to, and it is the only identity the app ever has.

   ------------------------------------------------------------------------------------
   BEFORE RUNNING: replace the three placeholders below.

     @MigrationGroup   display name of the Entra group that applies migrations
     @WebAppName       the App Service name — this is ALSO the managed identity's display
                       name, which is what CREATE USER ... FROM EXTERNAL PROVIDER matches on.
                       Take it from the deployment output `webAppName`, do not guess it.
     @FunctionAppName  same, for the Functions app (background work). Comment out the
                       Functions block if it is not deployed yet.
   ===================================================================================== */

SET NOCOUNT ON;

DECLARE @MigrationGroup  sysname = N'FCTelecom-SQL-Migrators';
DECLARE @WebAppName      sysname = N'REPLACE-with-webAppName-output';
DECLARE @FunctionAppName sysname = N'REPLACE-with-functionAppName-output';

DECLARE @sql nvarchar(max);

PRINT '=== Database principals ===';
PRINT '';

/* ── 1. Migration identity ──────────────────────────────────────────────────────────── */
PRINT '1. Migration identity: ' + @MigrationGroup;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @MigrationGroup)
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@MigrationGroup) + N' FROM EXTERNAL PROVIDER;';
    EXEC sp_executesql @sql;
    PRINT '   created';
END
ELSE PRINT '   already exists';

SET @sql = N'
    ALTER ROLE db_ddladmin   ADD MEMBER ' + QUOTENAME(@MigrationGroup) + N';
    ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@MigrationGroup) + N';
    ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@MigrationGroup) + N';';
EXEC sp_executesql @sql;
PRINT '   granted db_ddladmin, db_datareader, db_datawriter';
PRINT '   NOT granted db_owner - migrations do not need to change permissions or drop the database.';
PRINT '';

/* ── 2. Runtime identity: web application ───────────────────────────────────────────── */
PRINT '2. Runtime identity: ' + @WebAppName;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @WebAppName)
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@WebAppName) + N' FROM EXTERNAL PROVIDER;';
    EXEC sp_executesql @sql;
    PRINT '   created from external provider (system-assigned managed identity)';
END
ELSE PRINT '   already exists';

SET @sql = N'
    ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@WebAppName) + N';
    ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@WebAppName) + N';
    GRANT EXECUTE TO ' + QUOTENAME(@WebAppName) + N';';
EXEC sp_executesql @sql;
PRINT '   granted db_datareader, db_datawriter, EXECUTE';

/* Explicit DENY rather than merely withholding the grant. Withholding relies on nobody ever
   adding the app to a broader role by accident; DENY survives that, because DENY beats GRANT
   at every level in SQL Server's permission model. */
SET @sql = N'
    DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA
        TO ' + QUOTENAME(@WebAppName) + N';
    DENY ALTER ANY SCHEMA, ALTER ANY USER, ALTER ANY ROLE, ALTER ANY DATABASE DDL TRIGGER
        TO ' + QUOTENAME(@WebAppName) + N';';
BEGIN TRY
    EXEC sp_executesql @sql;
    PRINT '   DENIED schema-modifying permissions';
END TRY
BEGIN CATCH
    PRINT '   NOTE: some DENY statements were rejected: ' + ERROR_MESSAGE();
    PRINT '         Azure SQL supports a subset; the db_ddladmin absence is the primary control.';
END CATCH

/* The audit trail. An application that can rewrite its own audit log does not have one. */
IF OBJECT_ID('dbo.AuditEntries', 'U') IS NOT NULL
BEGIN
    SET @sql = N'DENY UPDATE, DELETE ON dbo.AuditEntries TO ' + QUOTENAME(@WebAppName) + N';';
    EXEC sp_executesql @sql;
    PRINT '   DENIED UPDATE, DELETE on dbo.AuditEntries';
END
ELSE PRINT '   WARNING: dbo.AuditEntries does not exist - apply the migration first, then re-run this script.';

/* Security events are append-only for the same reason. */
IF OBJECT_ID('dbo.SecurityEvents', 'U') IS NOT NULL
BEGIN
    SET @sql = N'DENY UPDATE, DELETE ON dbo.SecurityEvents TO ' + QUOTENAME(@WebAppName) + N';';
    EXEC sp_executesql @sql;
    PRINT '   DENIED UPDATE, DELETE on dbo.SecurityEvents';
END
PRINT '';

/* ── 3. Runtime identity: functions app ─────────────────────────────────────────────── */
IF @FunctionAppName NOT LIKE 'REPLACE%'
BEGIN
    PRINT '3. Runtime identity: ' + @FunctionAppName;

    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @FunctionAppName)
    BEGIN
        SET @sql = N'CREATE USER ' + QUOTENAME(@FunctionAppName) + N' FROM EXTERNAL PROVIDER;';
        EXEC sp_executesql @sql;
        PRINT '   created';
    END
    ELSE PRINT '   already exists';

    SET @sql = N'
        ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@FunctionAppName) + N';
        ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@FunctionAppName) + N';
        GRANT EXECUTE TO ' + QUOTENAME(@FunctionAppName) + N';';
    EXEC sp_executesql @sql;

    IF OBJECT_ID('dbo.AuditEntries', 'U') IS NOT NULL
    BEGIN
        SET @sql = N'DENY UPDATE, DELETE ON dbo.AuditEntries TO ' + QUOTENAME(@FunctionAppName) + N';';
        EXEC sp_executesql @sql;
    END
    PRINT '   granted reader/writer/execute, denied audit mutation';
END
ELSE PRINT '3. Functions app skipped (placeholder not replaced)';
PRINT '';

/* ── 4. Report what was actually granted ────────────────────────────────────────────── */
PRINT '4. Effective role membership';

SELECT
    dp.name                                   AS [principal],
    dp.type_desc                              AS [type],
    ISNULL(rp.name, '(no roles)')             AS [role]
FROM sys.database_principals dp
LEFT JOIN sys.database_role_members drm ON drm.member_principal_id = dp.principal_id
LEFT JOIN sys.database_principals rp    ON rp.principal_id = drm.role_principal_id
WHERE dp.name IN (@MigrationGroup, @WebAppName, @FunctionAppName)
ORDER BY dp.name, rp.name;

PRINT '';
PRINT 'EXPECTED:';
PRINT '  migration group  db_ddladmin + db_datareader + db_datawriter';
PRINT '  web app          db_datareader + db_datawriter ONLY';
PRINT '  functions app    db_datareader + db_datawriter ONLY';
PRINT '';
PRINT 'If either runtime identity shows db_ddladmin or db_owner, stop and fix it before';
PRINT 'continuing. Verify with 07-TestAppIdentity.ps1, which tests this from inside the app.';
PRINT '';
PRINT '=== End ===';
