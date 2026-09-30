/*
  Scanned images, patient documents, profile pictures ... are written into folders that are named by settings in the database. The
  sample database names folders on a Windows drive (C:\..., D:\...). Those do not exist on Linux / Docker / a Mac, where every upload would
  end up as a strangely named file inside the program's own folder - and be lost the next time the program is updated.

  When the program is started with a data folder (DANPHE_FILES_DIR - a Docker volume), every setting that names a Windows drive is pointed
  at a folder of its own below it. Settings that hold a Linux path (or anything else) are left alone, so this is safe to run at every start.
*/
SET NOCOUNT ON;
GO

UPDATE dbo.CORE_CFG_Parameters
SET ParameterValue = N'$(FILES_DIR)/' + REPLACE(REPLACE(ParameterGroupName + N'_' + ParameterName, N' ', N''), N'/', N'_') + N'/'
WHERE ParameterValue LIKE N'[A-Za-z]:[\/]%';
GO
