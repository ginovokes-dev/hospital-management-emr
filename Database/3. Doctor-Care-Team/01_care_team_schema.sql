/*
  Doctor care teams + doctor-to-doctor messages.
  Run against the EMR database (e.g. DEV_DanpheEMR_INT). Idempotent: safe to run more than once.

  What this adds
    - EMP_Employee.Speciality          free-text specialty shown next to a doctor's name
    - RBAC_Role.ConfineToCareTeam      1 = users whose roles are all "confined" only reach clinical screens and only see the
                                       patients in their care team (set for the sample Doctor roles below)
    - PAT_CareTeam                     which doctor(s) a patient is "under"
    - MSG_DoctorMessage                in-app messages between staff (with an optional patient attached)
    - PAT_CareTeamLog                  who searched for / added / shared / removed a patient (audit trail)
    - CORE_CFG_SetupLog                remembers one-time setup steps
    - triggers                         a doctor is added to a patient's care team automatically when they register the patient,
                                       when a visit is booked with them or when a visit is handed over to them
    - back-fill                        care teams for everything already in PAT_PatientVisits
*/
SET NOCOUNT ON;
GO

-- 1) specialty on the employee record ------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.EMP_Employee', 'Speciality') IS NULL
    ALTER TABLE dbo.EMP_Employee ADD Speciality varchar(100) NULL;
GO

-- 2) roles whose users are confined to their own patients ---------------------------------------------------------------
IF COL_LENGTH('dbo.RBAC_Role', 'ConfineToCareTeam') IS NULL
    ALTER TABLE dbo.RBAC_Role ADD ConfineToCareTeam bit NOT NULL CONSTRAINT DF_RBAC_Role_ConfineToCareTeam DEFAULT (0);
GO
UPDATE dbo.RBAC_Role SET ConfineToCareTeam = 1 WHERE RoleName IN ('Doctor', 'Amit Doctor') AND ISNULL(IsSysAdmin, 0) = 0;
GO

-- 2b) small log of one-time setup steps (e.g. "the admin login was seeded") so they are never repeated ------------------------
IF OBJECT_ID('dbo.CORE_CFG_SetupLog', 'U') IS NULL
    CREATE TABLE dbo.CORE_CFG_SetupLog
    (
        SetupKey  varchar(60)    NOT NULL CONSTRAINT PK_CORE_CFG_SetupLog PRIMARY KEY,
        Detail    nvarchar(300)  NULL,
        AppliedOn datetime       NOT NULL CONSTRAINT DF_CORE_CFG_SetupLog_On DEFAULT (GETDATE())
    );
GO

-- 3) care team -------------------------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.PAT_CareTeam', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PAT_CareTeam
    (
        CareTeamId        int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_PAT_CareTeam PRIMARY KEY,
        PatientId         int          NOT NULL CONSTRAINT FK_PAT_CareTeam_Patient REFERENCES dbo.PAT_Patient (PatientId),
        DoctorEmployeeId  int          NOT NULL CONSTRAINT FK_PAT_CareTeam_Doctor  REFERENCES dbo.EMP_Employee (EmployeeId),
        Relationship      varchar(20)  NOT NULL CONSTRAINT DF_PAT_CareTeam_Relationship DEFAULT ('Primary'), -- Primary | Visit | Shared | Self
        AddedByEmployeeId int          NULL,
        AddedOn           datetime     NOT NULL CONSTRAINT DF_PAT_CareTeam_AddedOn DEFAULT (GETDATE()),
        IsActive          bit          NOT NULL CONSTRAINT DF_PAT_CareTeam_IsActive DEFAULT (1),
        EndedOn           datetime     NULL,
        EndedByEmployeeId int          NULL,
        Note              nvarchar(500) NULL,
        CONSTRAINT UQ_PAT_CareTeam UNIQUE (PatientId, DoctorEmployeeId)
    );
    CREATE INDEX IX_PAT_CareTeam_Doctor ON dbo.PAT_CareTeam (DoctorEmployeeId, IsActive) INCLUDE (PatientId);
END
GO

-- 4) messages ---------------------------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.MSG_DoctorMessage', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MSG_DoctorMessage
    (
        MessageId          int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MSG_DoctorMessage PRIMARY KEY,
        FromEmployeeId     int            NOT NULL CONSTRAINT FK_MSG_From REFERENCES dbo.EMP_Employee (EmployeeId),
        ToEmployeeId       int            NOT NULL CONSTRAINT FK_MSG_To   REFERENCES dbo.EMP_Employee (EmployeeId),
        Subject            nvarchar(200)  NULL,
        Body               nvarchar(4000) NOT NULL,
        PatientId          int            NULL CONSTRAINT FK_MSG_Patient REFERENCES dbo.PAT_Patient (PatientId),
        MessageType        varchar(20)    NOT NULL CONSTRAINT DF_MSG_Type DEFAULT ('Message'),              -- Message | PatientShare | CareUpdate
        SentOn             datetime       NOT NULL CONSTRAINT DF_MSG_SentOn DEFAULT (GETDATE()),
        IsRead             bit            NOT NULL CONSTRAINT DF_MSG_IsRead DEFAULT (0),
        ReadOn             datetime       NULL,
        DeletedBySender    bit            NOT NULL CONSTRAINT DF_MSG_DelSender DEFAULT (0),
        DeletedByRecipient bit            NOT NULL CONSTRAINT DF_MSG_DelRecipient DEFAULT (0)
    );
    CREATE INDEX IX_MSG_To   ON dbo.MSG_DoctorMessage (ToEmployeeId,   DeletedByRecipient, SentOn DESC) INCLUDE (IsRead);
    CREATE INDEX IX_MSG_From ON dbo.MSG_DoctorMessage (FromEmployeeId, DeletedBySender,    SentOn DESC);
END
GO

-- 5) audit trail --------------------------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.PAT_CareTeamLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PAT_CareTeamLog
    (
        LogId             int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_PAT_CareTeamLog PRIMARY KEY,
        ActorEmployeeId   int           NOT NULL,
        Action            varchar(30)   NOT NULL,     -- Search | ViewSummary | AddSelf | Share | RemoveSelf | AdminAddDoctor | AdminEditDoctor | AdminDeactivate | AdminReactivate | AdminResetPassword
        PatientId         int           NULL,
        TargetEmployeeId  int           NULL,
        Detail            nvarchar(300) NULL,
        LoggedOn          datetime      NOT NULL CONSTRAINT DF_PAT_CareTeamLog_On DEFAULT (GETDATE())
    );
    CREATE INDEX IX_PAT_CareTeamLog_Patient ON dbo.PAT_CareTeamLog (PatientId, LoggedOn DESC);
END
GO

-- 6) triggers: keep care teams up to date without touching any application code path -------------------------------------
CREATE OR ALTER TRIGGER dbo.TRG_PAT_Patient_CareTeam ON dbo.PAT_Patient AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    -- a doctor who registers a patient is automatically on that patient's care team
    INSERT dbo.PAT_CareTeam (PatientId, DoctorEmployeeId, Relationship, AddedByEmployeeId, Note)
    SELECT i.PatientId, e.EmployeeId, 'Primary', e.EmployeeId, N'Registered by this doctor'
      FROM inserted i
      JOIN dbo.EMP_Employee e ON e.EmployeeId = i.CreatedBy AND e.IsAppointmentApplicable = 1 AND e.IsActive = 1
     WHERE NOT EXISTS (SELECT 1 FROM dbo.PAT_CareTeam c WHERE c.PatientId = i.PatientId AND c.DoctorEmployeeId = e.EmployeeId);
END
GO

CREATE OR ALTER TRIGGER dbo.TRG_PAT_PatientVisits_CareTeam ON dbo.PAT_PatientVisits AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    -- only when a visit is created or handed to another doctor
    IF NOT EXISTS (SELECT 1 FROM deleted) OR UPDATE(PerformerId)
    BEGIN
        -- booking a visit with a doctor puts the patient under that doctor (and re-activates an earlier withdrawal)
        UPDATE c SET IsActive = 1, EndedOn = NULL, EndedByEmployeeId = NULL
          FROM dbo.PAT_CareTeam c
          JOIN inserted i ON i.PatientId = c.PatientId AND i.PerformerId = c.DoctorEmployeeId
         WHERE c.IsActive = 0;

        INSERT dbo.PAT_CareTeam (PatientId, DoctorEmployeeId, Relationship, AddedByEmployeeId, Note)
        SELECT DISTINCT i.PatientId, i.PerformerId, 'Visit', i.CreatedBy, N'Visit booked with this doctor'
          FROM inserted i
          JOIN dbo.EMP_Employee e ON e.EmployeeId = i.PerformerId AND e.IsActive = 1
         WHERE i.PerformerId IS NOT NULL AND i.PatientId IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM dbo.PAT_CareTeam c WHERE c.PatientId = i.PatientId AND c.DoctorEmployeeId = i.PerformerId);
    END
END
GO

-- 7) back-fill from visits that already exist ---------------------------------------------------------------------------
INSERT dbo.PAT_CareTeam (PatientId, DoctorEmployeeId, Relationship, AddedByEmployeeId, AddedOn, Note)
SELECT v.PatientId, v.PerformerId, 'Visit', NULL, ISNULL(MIN(v.CreatedOn), GETDATE()), N'From earlier visits'
  FROM dbo.PAT_PatientVisits v
  JOIN dbo.EMP_Employee e ON e.EmployeeId = v.PerformerId
  JOIN dbo.PAT_Patient  p ON p.PatientId  = v.PatientId
 WHERE v.PerformerId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.PAT_CareTeam c WHERE c.PatientId = v.PatientId AND c.DoctorEmployeeId = v.PerformerId)
 GROUP BY v.PatientId, v.PerformerId;
GO
