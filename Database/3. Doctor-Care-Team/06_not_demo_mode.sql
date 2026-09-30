/*
  The sample database is marked as a "demo version" (parameter Demo / DemoVersion). In that mode the application hides the
  "Change Password" button, so an account that has to choose a new password at its first sign-in (every doctor the administrator
  creates) could never do it. This system is meant for real use: switch the demo mark off. It does nothing when it is off already.
*/
SET NOCOUNT ON;
GO

UPDATE dbo.CORE_CFG_Parameters
SET ParameterValue = JSON_MODIFY(ParameterValue, '$.IsDemoVersion', CAST(0 AS bit))
WHERE ParameterGroupName = 'Demo'
  AND ParameterName = 'DemoVersion'
  AND ISJSON(ParameterValue) = 1
  AND JSON_VALUE(ParameterValue, '$.IsDemoVersion') = 'true';
GO
