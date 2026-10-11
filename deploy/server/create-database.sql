/*
    JobMatch Ghana: one-time database + login setup on the VPS SQL Server.

    Run as a SQL Server sysadmin, on the server (both variables are required):
        sqlcmd -S . -E -b -i create-database.sql -v DatabaseName="JobMatchGhana" AppPool="JobMatch"

    The API connects with Windows authentication as its IIS app pool identity
    (IIS APPPOOL\<AppPool>), so there is no SQL password to store or rotate. That login gets
    read/write on this database only, and no access to the other databases on the shared server.
    Schema changes (migrate.sql) are applied by Deploy-Release.ps1 under an admin account,
    so the app identity needs no DDL rights.

    Idempotent: safe to re-run.
*/

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    CREATE DATABASE [$(DatabaseName)];
    PRINT 'Created database $(DatabaseName).';
END
GO

IF SUSER_ID(N'IIS APPPOOL\$(AppPool)') IS NULL
BEGIN
    CREATE LOGIN [IIS APPPOOL\$(AppPool)] FROM WINDOWS;
    PRINT 'Created login IIS APPPOOL\$(AppPool).';
END
GO

USE [$(DatabaseName)];
GO

IF USER_ID(N'IIS APPPOOL\$(AppPool)') IS NULL
    CREATE USER [IIS APPPOOL\$(AppPool)] FOR LOGIN [IIS APPPOOL\$(AppPool)];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\$(AppPool)];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\$(AppPool)];
PRINT 'IIS APPPOOL\$(AppPool) has read/write on $(DatabaseName).';
GO
