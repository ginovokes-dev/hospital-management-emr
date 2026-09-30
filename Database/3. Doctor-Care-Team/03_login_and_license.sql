/*
  First-run setup: the admin login and the software licence. Runs ONCE per database (it leaves a note in CORE_CFG_SetupLog),
  so a password the admin changes later is never put back.

  Danphe stores passwords (and the licence dates) with its own reversible scheme (3DES-ECB, key = MD5("Danphesalt"), PKCS7,
  base64 - see RBAC.EncryptPassword), so the two values below are computed outside SQL:

      $(ADMIN_PASSWORD_ENC)   the admin password, encrypted     (docker/encrypt-for-danphe.sh 123)
      $(LICENSE_END_ENC)      the licence end date, encrypted   (docker/encrypt-for-danphe.sh 2099-12-31)

  The web app fills these in itself at start-up (see DatabaseUpgrader.cs); docker/apply-database-changes.sh does the same for
  a manual run. Usernames are matched case-insensitively by the application (Admin, ADMIN and admin all work).
*/
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.CORE_CFG_SetupLog WHERE SetupKey = 'FirstRunAdminLoginAndLicence')
BEGIN
    UPDATE dbo.RBAC_User
       SET Password = N'$(ADMIN_PASSWORD_ENC)', IsActive = 1, NeedsPasswordUpdate = 0, ModifiedOn = GETDATE()
     WHERE UserName = 'admin';

    -- keep the licence type / start date / notice period, only move the end date
    UPDATE dbo.CORE_CFG_Parameters
       SET ParameterValue = JSON_MODIFY(ParameterValue, '$.EndDate', N'$(LICENSE_END_ENC)')
     WHERE ParameterGroupName = 'TenantMgnt' AND ParameterName = 'SoftwareLicense';

    INSERT dbo.CORE_CFG_SetupLog (SetupKey, Detail) VALUES ('FirstRunAdminLoginAndLicence', N'admin password set, licence end date moved');
END
GO
