/*
  OPTIONAL "fresh start" for the sample database that ships with the repository. Runs ONCE and only when asked for
  (DANPHE_FRESH_START=1 in the container set-up) - never automatically against a database that is already in use.

    - switches off the sample staff logins (Pooja, Amit, Billing, billing1); only 'admin' stays, and the administrator then adds
      the real doctors in "Manage Doctors"
    - hides the made-up sample patients (registered in 2023, before this software was set up) from every patient list and search:
      they are switched off, not deleted
    - switches off the unused sample role "Amit Doctor" so the role list only offers "Doctor" (and the other real roles)

  Hospital master data (departments, tests, prices, employees ...) is left untouched.
*/
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.CORE_CFG_SetupLog WHERE SetupKey = 'FreshStart')
BEGIN
    UPDATE dbo.RBAC_User SET IsActive = 0, ModifiedOn = GETDATE() WHERE UserName <> 'admin' AND IsActive = 1;

    UPDATE dbo.PAT_Patient SET IsActive = 0, ModifiedOn = GETDATE() WHERE IsActive = 1 AND CreatedOn < '20240101';

    UPDATE r SET r.IsActive = 0, r.ModifiedOn = GETDATE()
      FROM dbo.RBAC_Role r
     WHERE r.RoleName = 'Amit Doctor' AND ISNULL(r.IsSysAdmin, 0) = 0
       AND NOT EXISTS (SELECT 1 FROM dbo.RBAC_MAP_UserRole m JOIN dbo.RBAC_User u ON u.UserId = m.UserId
                        WHERE m.RoleId = r.RoleId AND ISNULL(m.IsActive, 1) = 1 AND u.IsActive = 1);

    INSERT dbo.CORE_CFG_SetupLog (SetupKey, Detail) VALUES ('FreshStart', N'sample logins and sample patients switched off');
END
GO
