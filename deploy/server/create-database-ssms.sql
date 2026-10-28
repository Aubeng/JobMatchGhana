/*
    JobMatch Ghana: one-time database + login setup, SSMS version (no sqlcmd variables).
    Same effect as create-database.sql with DatabaseName=JobMatchGhana, AppPool=JobMatch.
    Run in SSMS, connected to the VPS's SQL Server as an administrator. Idempotent.

    The API connects with Windows authentication as its IIS app pool identity
    (IIS APPPOOL\JobMatch): read/write on this database only, nothing else on the server.
*/
IF DB_ID(N'JobMatchGhana') IS NULL
BEGIN
    CREATE DATABASE [JobMatchGhana];
    PRINT 'Created database JobMatchGhana.';
END
GO

IF SUSER_ID(N'IIS APPPOOL\JobMatch') IS NULL
BEGIN
    CREATE LOGIN [IIS APPPOOL\JobMatch] FROM WINDOWS;
    PRINT 'Created login IIS APPPOOL\JobMatch.';
END
GO

USE [JobMatchGhana];
GO

IF USER_ID(N'IIS APPPOOL\JobMatch') IS NULL
    CREATE USER [IIS APPPOOL\JobMatch] FOR LOGIN [IIS APPPOOL\JobMatch];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\JobMatch];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\JobMatch];
PRINT 'IIS APPPOOL\JobMatch has read/write on JobMatchGhana.';
GO
