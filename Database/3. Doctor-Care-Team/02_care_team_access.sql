/*
  Menu entries + permissions for the new doctor screens. Idempotent.

    Care Team   (sidebar, doctors)       My Patients | Find Patient | Messages
    Doctors     (sidebar, admin only)    add / edit / withdraw doctors and issue their logins

  "Admin only" works through the app's own RBAC: SuperAdmin implicitly holds every permission, and 'doctoradmin-view' is never
  granted to any other role. The API enforces the same rule on the server (see DanpheAccessPolicy.cs).
*/
SET NOCOUNT ON;
GO

DECLARE @creator int = 1;                                   -- the admin employee
DECLARE @appDoctors int = (SELECT ApplicationId FROM dbo.RBAC_Application WHERE ApplicationCode = 'DOC');
DECLARE @appSettings int = (SELECT ApplicationId FROM dbo.RBAC_Application WHERE ApplicationCode = 'SETT');

-- permissions -------------------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_Permission WHERE PermissionName = 'careteam-view')
    INSERT dbo.RBAC_Permission (PermissionName, Description, ApplicationId, CreatedBy, CreatedOn, IsActive)
    VALUES ('careteam-view', 'My patients, find a patient, share and message other doctors', @appDoctors, @creator, GETDATE(), 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_Permission WHERE PermissionName = 'doctoradmin-view')
    INSERT dbo.RBAC_Permission (PermissionName, Description, ApplicationId, CreatedBy, CreatedOn, IsActive)
    VALUES ('doctoradmin-view', 'Add, edit and withdraw doctors and their logins (admin only)', @appSettings, @creator, GETDATE(), 1);

DECLARE @pCare int = (SELECT PermissionId FROM dbo.RBAC_Permission WHERE PermissionName = 'careteam-view');
DECLARE @pAdmin int = (SELECT PermissionId FROM dbo.RBAC_Permission WHERE PermissionName = 'doctoradmin-view');

-- routes (sidebar entries and the tabs inside the Care Team screen) ------------------------------------------------------
-- both entries sit at the very top of the menu (the existing 'Doctor' entry is 1)
DECLARE @seq int = -1;

IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam')
    INSERT dbo.RBAC_RouteConfig (RouteName, RouteDescription, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, Css, DefaultShow, DisplaySeq, IsActive)
    VALUES ('CareTeam', 'Doctor care team', 'Care Team', 'CareTeam', 'CareTeam', @pCare, NULL, 'patient.png', 1, @seq + 1, 1);
DECLARE @rCare int = (SELECT RouteId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam');

IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam/MyPatients')
    INSERT dbo.RBAC_RouteConfig (RouteName, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, DefaultShow, DisplaySeq, IsActive)
    VALUES ('CareTeamMyPatients', 'My Patients', 'CareTeam/MyPatients', 'MyPatients', @pCare, @rCare, 1, 1, 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam/FindPatient')
    INSERT dbo.RBAC_RouteConfig (RouteName, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, DefaultShow, DisplaySeq, IsActive)
    VALUES ('CareTeamFindPatient', 'Find Patient', 'CareTeam/FindPatient', 'FindPatient', @pCare, @rCare, 1, 2, 1);
IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam/Messages')
    INSERT dbo.RBAC_RouteConfig (RouteName, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, DefaultShow, DisplaySeq, IsActive)
    VALUES ('CareTeamMessages', 'Messages', 'CareTeam/Messages', 'Messages', @pCare, @rCare, 1, 3, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'DoctorAdmin')
    INSERT dbo.RBAC_RouteConfig (RouteName, RouteDescription, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, Css, DefaultShow, DisplaySeq, IsActive)
    VALUES ('DoctorAdmin', 'Manage doctors and their logins', 'Manage Doctors', 'DoctorAdmin', 'DoctorAdmin', @pAdmin, NULL, 'administrator.png', 1, @seq + 1, 1);

-- The "OPD Summary" tab of a patient record opens two sub-tabs ("Create Summary", "Summary History") that the application checks
-- against this table like every other screen, but the sample data has no rows for them, so the tab always ended on "Unauthorized".
DECLARE @rVisitSummary int = (SELECT TOP 1 RouteId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'Doctors/PatientOverviewMain/VisitSummary');
DECLARE @pVisitSummary int = (SELECT TOP 1 PermissionId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'Doctors/PatientOverviewMain/VisitSummary');
IF @rVisitSummary IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'Doctors/PatientOverviewMain/VisitSummary/VisitSummaryCreate')
    INSERT dbo.RBAC_RouteConfig (RouteName, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, DefaultShow, DisplaySeq, IsActive)
    VALUES ('VisitSummaryCreate', 'Create Summary', 'Doctors/PatientOverviewMain/VisitSummary/VisitSummaryCreate', 'VisitSummaryCreate', @pVisitSummary, @rVisitSummary, 1, 1, 1);
IF @rVisitSummary IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'Doctors/PatientOverviewMain/VisitSummary/SummaryHistory')
    INSERT dbo.RBAC_RouteConfig (RouteName, DisplayName, UrlFullPath, RouterLink, PermissionId, ParentRouteId, DefaultShow, DisplaySeq, IsActive)
    VALUES ('VisitSummaryHistory', 'Summary History', 'Doctors/PatientOverviewMain/VisitSummary/SummaryHistory', 'SummaryHistory', @pVisitSummary, @rVisitSummary, 1, 2, 1);

-- (databases prepared with an earlier version of this script had them at the bottom of the menu)
UPDATE dbo.RBAC_RouteConfig SET DisplaySeq = 0 WHERE UrlFullPath IN ('CareTeam', 'DoctorAdmin') AND DisplaySeq >= 40;

-- who gets what -------------------------------------------------------------------------------------------------------------
-- Care Team screens: every clinician role. (Manage Doctors is deliberately not mapped to any role: only SuperAdmin has it.)
INSERT dbo.RBAC_MAP_RolePermission (RoleId, PermissionId, CreatedBy, CreatedOn, IsActive)
SELECT r.RoleId, @pCare, @creator, GETDATE(), 1
  FROM dbo.RBAC_Role r
 WHERE r.ConfineToCareTeam = 1 AND ISNULL(r.IsSysAdmin, 0) = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.RBAC_MAP_RolePermission m WHERE m.RoleId = r.RoleId AND m.PermissionId = @pCare);

-- The sample "Doctor" roles only hold 11 of the ~30 permissions behind the app's own Doctors screens (no vitals, allergies,
-- medication, problems/history, orders, progress notes ...). Give clinician roles the complete set the app defines for doctors.
-- Left out: "OPD Summary" (a form the hospital has to design first - the sample database has none, so the tab only shows an error).
DECLARE @notOffered TABLE (PermissionName varchar(200) PRIMARY KEY);
INSERT @notOffered VALUES ('opd-summary-view');

INSERT dbo.RBAC_MAP_RolePermission (RoleId, PermissionId, CreatedBy, CreatedOn, IsActive)
SELECT r.RoleId, x.PermissionId, @creator, GETDATE(), 1
  FROM dbo.RBAC_Role r
 CROSS JOIN (SELECT DISTINCT rc.PermissionId
               FROM dbo.RBAC_RouteConfig rc
               JOIN dbo.RBAC_Permission p ON p.PermissionId = rc.PermissionId AND p.IsActive = 1
              WHERE rc.IsActive = 1 AND rc.UrlFullPath LIKE 'Doctors%'
                AND p.PermissionName NOT IN (SELECT PermissionName FROM @notOffered)) x
 WHERE r.ConfineToCareTeam = 1 AND ISNULL(r.IsSysAdmin, 0) = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.RBAC_MAP_RolePermission m WHERE m.RoleId = r.RoleId AND m.PermissionId = x.PermissionId);
-- ... and re-activate any that exist but were switched off
UPDATE m SET IsActive = 1
  FROM dbo.RBAC_MAP_RolePermission m
  JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId AND r.ConfineToCareTeam = 1 AND ISNULL(r.IsSysAdmin, 0) = 0
  JOIN dbo.RBAC_RouteConfig rc ON rc.PermissionId = m.PermissionId AND rc.UrlFullPath LIKE 'Doctors%' AND rc.IsActive = 1
  JOIN dbo.RBAC_Permission p ON p.PermissionId = m.PermissionId AND p.PermissionName NOT IN (SELECT PermissionName FROM @notOffered)
 WHERE ISNULL(m.IsActive, 0) = 0;
-- ... and take the ones that are not offered away from the clinician roles
UPDATE m SET IsActive = 0
  FROM dbo.RBAC_MAP_RolePermission m
  JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId AND r.ConfineToCareTeam = 1 AND ISNULL(r.IsSysAdmin, 0) = 0
  JOIN dbo.RBAC_Permission p ON p.PermissionId = m.PermissionId AND p.PermissionName IN (SELECT PermissionName FROM @notOffered)
 WHERE ISNULL(m.IsActive, 0) = 1;

-- the administrator lands on "Manage Doctors" after login (unless a landing page has been chosen for the role already)
UPDATE dbo.RBAC_Role SET DefaultRouteId = (SELECT RouteId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'DoctorAdmin')
 WHERE ISNULL(IsSysAdmin, 0) = 1 AND DefaultRouteId IS NULL;

-- clinicians land on "My Patients" after login unless the user has a personal landing page already
UPDATE dbo.RBAC_Role SET DefaultRouteId = (SELECT RouteId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath = 'CareTeam/MyPatients')
 WHERE ConfineToCareTeam = 1 AND ISNULL(IsSysAdmin, 0) = 0;

-- ... and each login that has no landing page of its own opens on its role's page (My Patients / Manage Doctors)
UPDATE u SET LandingPageRouteId = r.DefaultRouteId
  FROM dbo.RBAC_User u
  JOIN dbo.RBAC_MAP_UserRole m ON m.UserId = u.UserId AND ISNULL(m.IsActive, 1) = 1
  JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId AND ISNULL(r.IsActive, 1) = 1
 WHERE u.LandingPageRouteId IS NULL
   AND r.DefaultRouteId IN (SELECT RouteId FROM dbo.RBAC_RouteConfig WHERE UrlFullPath IN ('CareTeam/MyPatients', 'DoctorAdmin'));
GO
