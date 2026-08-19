-- Data-plane validation — docs/11 §4.3
--
-- Run as the APPLICATION's managed identity, not as an admin. The whole point of several of
-- these checks is to confirm the application cannot do something, and running them as an
-- administrator will cheerfully report that it can.
--
--   sqlcmd -S <server>.database.windows.net -d fctelecom -G -Q "..."   (Entra auth)
--
-- Each block prints PASS or FAIL. Nothing here modifies data except the audit-immutability
-- test, which is expected to be rejected and is wrapped in a transaction that always rolls
-- back regardless.

SET NOCOUNT ON;
PRINT '=== Data-plane validation ===';
PRINT '';

-- ── 1. Who am I, and how did I connect? ────────────────────────────────────────────────
--
-- If this reports a SQL login rather than an Entra principal, the connection string still has
-- a credential in it somewhere and the managed-identity path is not actually being used.
PRINT '1. Connection identity';
SELECT
    SUSER_SNAME()                         AS [login],
    USER_NAME()                           AS [database_user],
    CASE auth_scheme WHEN 'NTLM' THEN 'NTLM' ELSE auth_scheme END AS [auth_scheme]
FROM sys.dm_exec_connections
WHERE session_id = @@SPID;
PRINT '   Expect an Entra principal. A SQL login here means a credential is still in config.';
PRINT '';

-- ── 2. Audit immutability ──────────────────────────────────────────────────────────────
--
-- DENY UPDATE, DELETE ON dbo.AuditEntries should be in force. An audit trail the application
-- can rewrite is not an audit trail. The rollback is belt and braces: the statement should
-- fail before it ever commits.
PRINT '2. Audit table immutability';
BEGIN TRY
    BEGIN TRANSACTION;
        UPDATE TOP (1) dbo.AuditEntries SET [Action] = [Action];
    ROLLBACK TRANSACTION;
    PRINT '   FAIL - UPDATE on dbo.AuditEntries succeeded. Apply the DENY.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    IF ERROR_NUMBER() = 229
        PRINT '   PASS - UPDATE denied (permission).';
    ELSE
        PRINT '   CHECK - UPDATE failed with error ' + CAST(ERROR_NUMBER() AS varchar(10))
              + ': ' + ERROR_MESSAGE() + ' (expected 229 = permission denied)';
END CATCH;

BEGIN TRY
    BEGIN TRANSACTION;
        DELETE TOP (1) FROM dbo.AuditEntries;
    ROLLBACK TRANSACTION;
    PRINT '   FAIL - DELETE on dbo.AuditEntries succeeded. Apply the DENY.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    IF ERROR_NUMBER() = 229
        PRINT '   PASS - DELETE denied (permission).';
    ELSE
        PRINT '   CHECK - DELETE failed with error ' + CAST(ERROR_NUMBER() AS varchar(10))
              + ': ' + ERROR_MESSAGE();
END CATCH;
PRINT '';

-- ── 3. Concurrency tokens are real rowversion columns ──────────────────────────────────
--
-- This is the schema-level confirmation of the defect fixed during the validation pass: a
-- RowVersion column that is varbinary rather than rowversion exists, is never populated, and
-- silently permits lost updates.
PRINT '3. RowVersion column types';
SELECT
    t.name                AS [table],
    ty.name               AS [type],
    CASE WHEN ty.name = 'timestamp' THEN 'PASS' ELSE 'FAIL' END AS [result]
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
WHERE c.name = 'RowVersion'
ORDER BY [result] DESC, t.name;
PRINT '   sys.types reports rowversion as ''timestamp''. Anything else is a FAIL.';
PRINT '';

-- ── 4. Check constraints survived the migration ────────────────────────────────────────
--
-- These are the constraints that make the append-only cost history actually append-only. If
-- the migration dropped one, the schema still looks right and the invariant is gone.
PRINT '4. Expected check constraints';
;WITH expected([name]) AS (
    SELECT 'CK_ServiceCosts_EffectiveRange' UNION ALL
    SELECT 'CK_ServiceDependencies_NotSelf'
)
SELECT
    e.[name],
    CASE WHEN cc.[name] IS NULL THEN 'MISSING' ELSE 'present' END AS [result]
FROM expected e
LEFT JOIN sys.check_constraints cc ON cc.[name] = e.[name];
PRINT '';

-- ── 5. One open cost row per service ───────────────────────────────────────────────────
--
-- The filtered unique index is what enforces it. Confirm the index exists AND that the data
-- actually obeys it — a seeded environment can violate an index that was created later.
PRINT '5. Effective-dated cost history';
SELECT
    i.[name] AS [index],
    i.filter_definition,
    CASE WHEN i.is_unique = 1 THEN 'unique' ELSE 'NOT UNIQUE - FAIL' END AS [uniqueness]
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE t.name = 'ServiceCosts' AND i.has_filter = 1;

SELECT TOP (10)
    ServiceId,
    COUNT(*) AS [open_rows]
FROM dbo.ServiceCosts
WHERE EffectiveTo IS NULL
GROUP BY ServiceId
HAVING COUNT(*) > 1;
PRINT '   The second result set should be EMPTY. Any row is a service with two open costs.';
PRINT '';

-- ── 6. Reporting schema separation ─────────────────────────────────────────────────────
--
-- Run this block as the REPORTING principal, not the application. It should be able to read
-- rpt.* and should fail on dbo.*. Failing to fail is the finding.
PRINT '6. Reporting principal separation - run this block as the reporting login';
PRINT '   SELECT TOP 1 * FROM rpt.<view>;    -- expect success';
PRINT '   SELECT TOP 1 * FROM dbo.Services;  -- expect error 229, permission denied';
PRINT '';

PRINT '=== End ===';
