/*
  Patient files, scanned images and eye-scan images are stored by the original database in FILESTREAM tables. SQL Server for Linux
  (and so Docker on a Mac) has no FILESTREAM, so the sample database restores without them and every upload would fail.

  On a Linux SQL Server this recreates the three tables as ordinary tables (same names, same columns; the file bytes go into a normal
  varbinary(max) column), so uploading a scan or a document works. It changes nothing on Windows, where FILESTREAM works, and it does
  nothing once a table has been converted. The tables it replaces are empty (their data cannot be restored on Linux); SQL Server will
  not let an offline FILESTREAM table be dropped, so the old, empty, unusable table is renamed to <name>_FilestreamOffline and left there.
*/
SET NOCOUNT ON;
GO

IF EXISTS (SELECT 1 FROM sys.dm_os_host_info WHERE host_platform = 'Linux')
   AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PAT_PatientFiles' AND filestream_data_space_id IS NOT NULL)
BEGIN
    EXEC sp_rename 'dbo.PAT_PatientFiles', 'PAT_PatientFiles_FilestreamOffline';
    CREATE TABLE dbo.PAT_PatientFiles
    (
        PatientFileId  bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_PAT_PatientFiles_Linux PRIMARY KEY,
        PatientId      int              NOT NULL,
        ROWGUID        uniqueidentifier NOT NULL CONSTRAINT DF_PAT_PatientFiles_ROWGUID DEFAULT (NEWID()),
        FileType       varchar(50)      NULL,
        Description    varchar(200)     NULL,
        FileBinaryData varbinary(max)   NULL,
        FileName       varchar(200)     NULL,
        FileNo         int              NULL,
        Title          varchar(200)     NULL,
        FileExtention  varchar(50)      NULL,
        UploadedOn     datetime         NULL,
        UploadedBy     int              NULL,
        IsActive       bit              NULL,
        ImageFullPath  varchar(500)     NULL,
        CONSTRAINT UQ_PAT_PatientFiles_ROWGUID_Linux UNIQUE (ROWGUID)
    );
END
GO

IF EXISTS (SELECT 1 FROM sys.dm_os_host_info WHERE host_platform = 'Linux')
   AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CLN_PAT_Images' AND filestream_data_space_id IS NOT NULL)
BEGIN
    EXEC sp_rename 'dbo.CLN_PAT_Images', 'CLN_PAT_Images_FilestreamOffline';
    CREATE TABLE dbo.CLN_PAT_Images
    (
        PatImageId     bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CLN_PAT_Images_Linux PRIMARY KEY,
        PatientId      int              NOT NULL,
        PatientVisitId int              NOT NULL,
        DepartmentId   int              NULL,
        ROWGUID        uniqueidentifier NOT NULL CONSTRAINT DF_CLN_PAT_Images_ROWGUID DEFAULT (NEWID()),
        FileType       varchar(50)      NULL,
        Comment        varchar(200)     NULL,
        FileBinaryData varbinary(max)   NULL,
        FileName       varchar(200)     NULL,
        Title          varchar(200)     NULL,
        FileExtention  varchar(50)      NULL,
        UploadedOn     datetime         NULL,
        UploadedBy     int              NULL,
        IsActive       bit              NULL,
        CONSTRAINT UQ_CLN_PAT_Images_ROWGUID_Linux UNIQUE (ROWGUID)
    );
END
GO

IF EXISTS (SELECT 1 FROM sys.dm_os_host_info WHERE host_platform = 'Linux')
   AND EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CLN_EyeScanImages' AND filestream_data_space_id IS NOT NULL)
BEGIN
    EXEC sp_rename 'dbo.CLN_EyeScanImages', 'CLN_EyeScanImages_FilestreamOffline';
    CREATE TABLE dbo.CLN_EyeScanImages
    (
        PatientFileId  bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CLN_EyeScanImages_Linux PRIMARY KEY,
        PatientId      int              NOT NULL,
        ROWGUID        uniqueidentifier NOT NULL CONSTRAINT DF_CLN_EyeScanImages_ROWGUID DEFAULT (NEWID()),
        FileType       varchar(50)      NULL,
        Description    varchar(200)     NULL,
        FileBinaryData varbinary(max)   NULL,
        FileName       varchar(200)     NULL,
        FileNo         int              NULL,
        Title          varchar(200)     NULL,
        FileExtention  varchar(50)      NULL,
        UploadedOn     datetime         NULL,
        UploadedBy     int              NULL,
        IsActive       bit              NULL,
        ImageFullPath  varchar(500)     NULL,
        CONSTRAINT UQ_CLN_EyeScanImages_ROWGUID_Linux UNIQUE (ROWGUID)
    );
END
GO
