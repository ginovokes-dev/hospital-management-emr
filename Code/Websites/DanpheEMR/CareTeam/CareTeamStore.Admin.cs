using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using DanpheEMR.Security;

namespace DanpheEMR.CareTeam
{
    public class StaffInput
    {
        public int EmployeeId { get; set; }
        public int UserId { get; set; }
        public string Salutation { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Gender { get; set; }
        public string Email { get; set; }
        public string ContactNumber { get; set; }
        public int? DepartmentId { get; set; }
        public string Speciality { get; set; }
        public string MedCertificationNo { get; set; }
        public int RoleId { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public bool MustChangePassword { get; set; }
    }

    /// <summary>what only the administrator may do: issue, change, withdraw and re-instate doctor (staff) logins</summary>
    public partial class CareTeamStore
    {
        private static readonly Regex UserNameRule = new Regex("^[A-Za-z0-9._-]{3,30}$", RegexOptions.Compiled);
        private static readonly Regex EmailRule = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        public Dictionary<string, object> AdminLookups()
        {
            return new Dictionary<string, object>
            {
                { "Roles", Rows(@"SELECT RoleId, RoleName, RoleDescription, CAST(ISNULL(ConfineToCareTeam, 0) AS bit) AS IsDoctorRole
                                    FROM dbo.RBAC_Role WHERE ISNULL(IsActive, 1) = 1 AND ISNULL(IsSysAdmin, 0) = 0
                                    ORDER BY ConfineToCareTeam DESC, CASE WHEN RoleName = 'Doctor' THEN 0 ELSE 1 END, RoleName") },
                { "Departments", Rows(@"SELECT DepartmentId, DepartmentName FROM dbo.MST_Department WHERE IsActive = 1 AND ISNULL(IsAppointmentApplicable, 0) = 1 ORDER BY DepartmentName") },
                { "Salutations", new[] { "Dr", "Prof", "Mr", "Mrs", "Ms", "Miss" } },
                { "Specialities", new[] { "General Practice", "Internal Medicine", "Cardiology", "Paediatrics", "Obstetrics & Gynaecology", "General Surgery", "Orthopaedics",
                                          "Neurology", "Psychiatry", "Dermatology", "ENT", "Ophthalmology", "Urology", "Nephrology", "Oncology", "Radiology", "Anaesthesia", "Emergency Medicine" } }
            };
        }

        public List<Dictionary<string, object>> StaffList()
        {
            return Rows(@"SELECT u.UserId, u.UserName, u.IsActive AS LoginActive, ISNULL(u.NeedsPasswordUpdate, 0) AS MustChangePassword,
                                 e.EmployeeId, e.Salutation, e.FirstName, e.MiddleName, e.LastName, e.FullName, e.Gender, e.Email, e.ContactNumber,
                                 e.DepartmentId, d.DepartmentName, e.Speciality, e.MedCertificationNo, e.IsActive AS EmployeeActive,
                                 r.RoleId, r.RoleName, ISNULL(r.IsSysAdmin, 0) AS IsSuperAdmin, ISNULL(r.ConfineToCareTeam, 0) AS IsDoctorRole,
                                 (SELECT COUNT(*) FROM dbo.PAT_CareTeam c WHERE c.DoctorEmployeeId = e.EmployeeId AND c.IsActive = 1) AS PatientCount,
                                 (SELECT MAX(l.LoggedOn) FROM dbo.PAT_CareTeamLog l WHERE l.ActorEmployeeId = e.EmployeeId) AS LastActivity
                            FROM dbo.RBAC_User u
                            JOIN dbo.EMP_Employee e ON e.EmployeeId = u.EmployeeId
                            LEFT JOIN dbo.MST_Department d ON d.DepartmentId = e.DepartmentId
                            OUTER APPLY (SELECT TOP 1 r2.RoleId, r2.RoleName, r2.IsSysAdmin, r2.ConfineToCareTeam
                                           FROM dbo.RBAC_MAP_UserRole m JOIN dbo.RBAC_Role r2 ON r2.RoleId = m.RoleId
                                          WHERE m.UserId = u.UserId AND ISNULL(m.IsActive, 1) = 1
                                          ORDER BY ISNULL(r2.IsSysAdmin, 0) DESC, m.UserRoleMapId DESC) r
                           ORDER BY ISNULL(r.IsSysAdmin, 0) DESC, u.IsActive DESC, e.FullName");
        }

        public List<Dictionary<string, object>> AccessLog(int take)
        {
            take = Math.Min(Math.Max(take, 1), 1000);
            var rows = Rows(@"SELECT TOP (@n) l.LogId, l.LoggedOn, l.Action, a.FullName AS Actor, l.PatientId, p.PatientCode, p.FirstName, p.MiddleName, p.LastName,
                                     t.FullName AS Target, l.Detail
                                FROM dbo.PAT_CareTeamLog l
                                LEFT JOIN dbo.EMP_Employee a ON a.EmployeeId = l.ActorEmployeeId
                                LEFT JOIN dbo.PAT_Patient p ON p.PatientId = l.PatientId
                                LEFT JOIN dbo.EMP_Employee t ON t.EmployeeId = l.TargetEmployeeId
                               ORDER BY l.LogId DESC", "@n", take);
            foreach (var r in rows) r["PatientName"] = r["PatientId"] == null ? null : NameOf(r);
            return rows;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        private static string Tidy(string s, int max, string what, bool required)
        {
            s = (s ?? "").Trim();
            if (required && s.Length == 0) throw new UserFacingException("Please enter " + what + ".");
            if (s.Length > max) throw new UserFacingException(what.Substring(0, 1).ToUpper() + what.Substring(1) + " is too long (" + max + " characters at most).");
            return s.Length == 0 ? null : s;
        }

        private static string FullNameOf(string salutation, string first, string middle, string last)
        {
            string pre = string.IsNullOrEmpty(salutation) ? "" : (salutation.EndsWith(".") ? salutation : salutation + ".") + " ";
            return (pre + first + " " + (string.IsNullOrEmpty(middle) ? "" : middle + " ") + last).Trim();
        }

        private static void CheckPassword(string password)
        {
            // 20 at most: the application's own "Change password" form does not take a longer current password
            if (string.IsNullOrEmpty(password) || password.Length < 6 || password.Length > 20) throw new UserFacingException("The password must be 6 to 20 characters long.");
        }

        private Dictionary<string, object> RoleFor(SqlConnection cn, SqlTransaction tx, int roleId)
        {
            var role = Rows(cn, tx, "SELECT RoleId, RoleName, ISNULL(IsSysAdmin, 0) AS IsSys, ISNULL(ConfineToCareTeam, 0) AS Confine FROM dbo.RBAC_Role WHERE RoleId = @r AND ISNULL(IsActive, 1) = 1", "@r", roleId).FirstOrDefault();
            if (role == null) throw new UserFacingException("Please choose a role.");
            if (ToBool(role["IsSys"])) throw new UserFacingException("The administrator role cannot be given from this page.");
            return role;
        }

        private void CheckDepartment(SqlConnection cn, SqlTransaction tx, StaffInput d, bool isDoctorRole)
        {
            if (isDoctorRole && !d.DepartmentId.HasValue) throw new UserFacingException("Please choose the doctor's speciality / department.");
            if (d.DepartmentId.HasValue && Scalar(cn, tx, "SELECT TOP 1 1 FROM dbo.MST_Department WHERE DepartmentId = @d AND IsActive = 1", "@d", d.DepartmentId.Value) == null)
                throw new UserFacingException("That department could not be found.");
        }

        // ---------------------------------------------------------------------------------------------------------------------
        public Dictionary<string, object> CreateStaff(CallerInfo admin, StaffInput d)
        {
            string first = Tidy(d.FirstName, 30, "the first name", true), last = Tidy(d.LastName, 30, "the last name", true), middle = Tidy(d.MiddleName, 30, "the middle name", false);
            string userName = (d.UserName ?? "").Trim();
            if (!UserNameRule.IsMatch(userName)) throw new UserFacingException("The username must be 3-30 letters, numbers, dots, dashes or underscores (no spaces).");
            CheckPassword(d.Password);
            string email = Tidy(d.Email, 100, "the e-mail address", false);
            if (email != null && !EmailRule.IsMatch(email)) throw new UserFacingException("That e-mail address does not look right.");
            string salutation = Tidy(d.Salutation, 10, "the title", false), gender = Tidy(d.Gender, 10, "the gender", false);
            string phone = Tidy(d.ContactNumber, 20, "the phone number", false), speciality = Tidy(d.Speciality, 100, "the speciality", false), cert = Tidy(d.MedCertificationNo, 20, "the registration number", false);

            int employeeId, userId;
            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                if (Scalar(cn, tx, "SELECT TOP 1 1 FROM dbo.RBAC_User WHERE LOWER(UserName) = LOWER(@u)", "@u", userName) != null)
                    throw new UserFacingException("The username '" + userName + "' is already taken. Please choose another.");
                var role = RoleFor(cn, tx, d.RoleId);
                bool isDoctorRole = ToBool(role["Confine"]);
                CheckDepartment(cn, tx, d, isDoctorRole);

                // the consultation-fee items new doctors are billed with: whatever the existing doctors use most
                var items = Rows(cn, tx, @"SELECT TOP 1 OpdNewPatientServiceItemId AS N, OpdOldPatientServiceItemId AS O, FollowupServiceItemId AS F, InternalReferralServiceItemId AS I
                                             FROM dbo.EMP_Employee WHERE OpdNewPatientServiceItemId IS NOT NULL
                                            GROUP BY OpdNewPatientServiceItemId, OpdOldPatientServiceItemId, FollowupServiceItemId, InternalReferralServiceItemId
                                            ORDER BY COUNT(*) DESC").FirstOrDefault();

                employeeId = ToInt(Scalar(cn, tx, @"INSERT dbo.EMP_Employee
                        (Salutation, FirstName, MiddleName, LastName, FullName, Gender, ContactNumber, DepartmentId, Email, EmployeeRoleId, EmployeeTypeId, MedCertificationNo,
                         CreatedBy, CreatedOn, IsActive, IsAppointmentApplicable, IsExternal, DisplaySequence,
                         OpdNewPatientServiceItemId, OpdOldPatientServiceItemId, FollowupServiceItemId, InternalReferralServiceItemId, Speciality)
                      VALUES (@sal, @fn, @mn, @ln, @full, @g, @ph, @dept, @mail, @erole, 1, @cert,
                              @by, GETDATE(), 1, @appt, 0, 0,
                              @n, @o, @f, @i, @spec);
                      SELECT CAST(SCOPE_IDENTITY() AS int);",
                    "@sal", salutation, "@fn", first, "@mn", middle, "@ln", last, "@full", FullNameOf(salutation, first, middle, last), "@g", gender, "@ph", phone,
                    "@dept", d.DepartmentId, "@mail", email, "@erole", isDoctorRole ? (object)29 : null, "@cert", cert, "@by", admin.EmployeeId,
                    "@appt", isDoctorRole ? 1 : 0,
                    "@n", items == null ? null : items["N"], "@o", items == null ? null : items["O"], "@f", items == null ? null : items["F"], "@i", items == null ? null : items["I"],
                    "@spec", speciality));

                // the page the login opens on (the role's default: My Patients for doctors)
                object landing = Scalar(cn, tx, "SELECT DefaultRouteId FROM dbo.RBAC_Role WHERE RoleId = @r", "@r", d.RoleId);
                userId = ToInt(Scalar(cn, tx, @"INSERT dbo.RBAC_User (EmployeeId, UserName, Password, Email, NeedsPasswordUpdate, CreatedBy, CreatedOn, IsActive, LandingPageRouteId)
                                                VALUES (@e, @u, @pw, @mail, @must, @by, GETDATE(), 1, @landing);
                                                SELECT CAST(SCOPE_IDENTITY() AS int);",
                    "@e", employeeId, "@u", userName, "@pw", RBAC.EncryptPassword(d.Password), "@mail", email, "@must", d.MustChangePassword ? 1 : 0, "@by", admin.EmployeeId, "@landing", landing));

                Execute(cn, tx, @"INSERT dbo.RBAC_MAP_UserRole (UserId, RoleId, CreatedBy, CreatedOn, IsActive) VALUES (@u, @r, @by, GETDATE(), 1)",
                    "@u", userId, "@r", d.RoleId, "@by", admin.EmployeeId);
                tx.Commit();
            }
            Log(admin.EmployeeId, "AdminAddDoctor", null, employeeId, userName);
            InvalidateCallers();
            return new Dictionary<string, object> { { "UserId", userId }, { "EmployeeId", employeeId }, { "UserName", userName } };
        }

        public void UpdateStaff(CallerInfo admin, StaffInput d)
        {
            string first = Tidy(d.FirstName, 30, "the first name", true), last = Tidy(d.LastName, 30, "the last name", true), middle = Tidy(d.MiddleName, 30, "the middle name", false);
            string email = Tidy(d.Email, 100, "the e-mail address", false);
            if (email != null && !EmailRule.IsMatch(email)) throw new UserFacingException("That e-mail address does not look right.");
            string salutation = Tidy(d.Salutation, 10, "the title", false), gender = Tidy(d.Gender, 10, "the gender", false);
            string phone = Tidy(d.ContactNumber, 20, "the phone number", false), speciality = Tidy(d.Speciality, 100, "the speciality", false), cert = Tidy(d.MedCertificationNo, 20, "the registration number", false);

            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                var target = Rows(cn, tx, "SELECT UserId, EmployeeId, UserName FROM dbo.RBAC_User WHERE UserId = @u", "@u", d.UserId).FirstOrDefault();
                if (target == null) throw new UserFacingException("That login could not be found.");
                int employeeId = ToInt(target["EmployeeId"]);
                bool targetIsAdmin = TargetIsSuperAdmin(cn, tx, d.UserId);

                bool isDoctorRole;
                if (targetIsAdmin)
                {
                    isDoctorRole = false;   // the administrator's role is never changed here
                }
                else
                {
                    var role = RoleFor(cn, tx, d.RoleId);
                    isDoctorRole = ToBool(role["Confine"]);
                    // the landing page follows the role unless someone picked a different one
                    Execute(cn, tx, @"UPDATE u SET LandingPageRouteId = nr.DefaultRouteId
                                        FROM dbo.RBAC_User u
                                        CROSS JOIN dbo.RBAC_Role nr
                                       WHERE u.UserId = @u AND nr.RoleId = @r AND nr.DefaultRouteId IS NOT NULL
                                         AND (u.LandingPageRouteId IS NULL OR u.LandingPageRouteId IN
                                              (SELECT r2.DefaultRouteId FROM dbo.RBAC_MAP_UserRole m2 JOIN dbo.RBAC_Role r2 ON r2.RoleId = m2.RoleId WHERE m2.UserId = @u AND m2.RoleId <> @r))",
                        "@u", d.UserId, "@r", d.RoleId);
                    // this login gets exactly this role
                    Execute(cn, tx, "UPDATE dbo.RBAC_MAP_UserRole SET IsActive = 0, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u AND RoleId <> @r AND ISNULL(IsActive, 1) = 1",
                        "@u", d.UserId, "@r", d.RoleId, "@by", admin.EmployeeId);
                    if (Execute(cn, tx, "UPDATE dbo.RBAC_MAP_UserRole SET IsActive = 1, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u AND RoleId = @r",
                            "@u", d.UserId, "@r", d.RoleId, "@by", admin.EmployeeId) == 0)
                        Execute(cn, tx, "INSERT dbo.RBAC_MAP_UserRole (UserId, RoleId, CreatedBy, CreatedOn, IsActive) VALUES (@u, @r, @by, GETDATE(), 1)",
                            "@u", d.UserId, "@r", d.RoleId, "@by", admin.EmployeeId);
                }
                CheckDepartment(cn, tx, d, isDoctorRole);

                Execute(cn, tx, @"UPDATE dbo.EMP_Employee
                                     SET Salutation = @sal, FirstName = @fn, MiddleName = @mn, LastName = @ln, FullName = @full, Gender = @g, ContactNumber = @ph,
                                         DepartmentId = @dept, Email = @mail, MedCertificationNo = @cert, Speciality = @spec, ModifiedBy = @by, ModifiedOn = GETDATE()
                                   WHERE EmployeeId = @e",
                    "@sal", salutation, "@fn", first, "@mn", middle, "@ln", last, "@full", FullNameOf(salutation, first, middle, last), "@g", gender, "@ph", phone,
                    "@dept", d.DepartmentId, "@mail", email, "@cert", cert, "@spec", speciality, "@by", admin.EmployeeId, "@e", employeeId);
                if (!targetIsAdmin)
                    Execute(cn, tx, "UPDATE dbo.EMP_Employee SET IsAppointmentApplicable = @appt, EmployeeRoleId = CASE WHEN @appt = 1 THEN ISNULL(EmployeeRoleId, 29) ELSE EmployeeRoleId END WHERE EmployeeId = @e",
                        "@appt", isDoctorRole ? 1 : 0, "@e", employeeId);
                Execute(cn, tx, "UPDATE dbo.RBAC_User SET Email = @mail, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u", "@mail", email, "@by", admin.EmployeeId, "@u", d.UserId);
                tx.Commit();
                Log(admin.EmployeeId, "AdminEditDoctor", null, employeeId, Convert.ToString(target["UserName"]));
            }
            InvalidateCallers();
        }

        private bool TargetIsSuperAdmin(SqlConnection cn, SqlTransaction tx, int userId)
        {
            return Scalar(cn, tx, @"SELECT TOP 1 1 FROM dbo.RBAC_MAP_UserRole m JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId
                                     WHERE m.UserId = @u AND ISNULL(m.IsActive, 1) = 1 AND ISNULL(r.IsSysAdmin, 0) = 1", "@u", userId) != null;
        }

        public void ResetPassword(CallerInfo admin, int userId, string newPassword, bool mustChange)
        {
            CheckPassword(newPassword);
            var target = Rows("SELECT UserName, EmployeeId FROM dbo.RBAC_User WHERE UserId = @u", "@u", userId).FirstOrDefault();
            if (target == null) throw new UserFacingException("That login could not be found.");
            Execute("UPDATE dbo.RBAC_User SET Password = @pw, NeedsPasswordUpdate = @must, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u",
                "@pw", RBAC.EncryptPassword(newPassword), "@must", mustChange ? 1 : 0, "@by", admin.EmployeeId, "@u", userId);
            Log(admin.EmployeeId, "AdminResetPassword", null, ToInt(target["EmployeeId"]), Convert.ToString(target["UserName"]));
        }

        /// <summary>switches the login (and the employee record) off. Nothing is deleted: the doctor's notes, orders and care-team history stay.</summary>
        public void Withdraw(CallerInfo admin, int userId, int? handOverToEmployeeId, string reason)
        {
            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                var target = Rows(cn, tx, "SELECT UserId, EmployeeId, UserName FROM dbo.RBAC_User WHERE UserId = @u", "@u", userId).FirstOrDefault();
                if (target == null) throw new UserFacingException("That login could not be found.");
                int employeeId = ToInt(target["EmployeeId"]);
                if (userId == admin.UserId) throw new UserFacingException("You cannot withdraw your own login.");
                if (TargetIsSuperAdmin(cn, tx, userId)) throw new UserFacingException("The administrator login cannot be withdrawn.");

                Execute(cn, tx, "UPDATE dbo.RBAC_User SET IsActive = 0, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u", "@by", admin.EmployeeId, "@u", userId);
                Execute(cn, tx, "UPDATE dbo.EMP_Employee SET IsActive = 0, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE EmployeeId = @e", "@by", admin.EmployeeId, "@e", employeeId);

                int handed = 0;
                if (handOverToEmployeeId.HasValue && handOverToEmployeeId.Value > 0 && handOverToEmployeeId.Value != employeeId)
                {
                    int to = handOverToEmployeeId.Value;
                    if (Scalar(cn, tx, @"SELECT TOP 1 1 FROM dbo.RBAC_User u JOIN dbo.EMP_Employee e ON e.EmployeeId = u.EmployeeId
                                          WHERE e.EmployeeId = @t AND u.IsActive = 1 AND e.IsActive = 1", "@t", to) == null)
                        throw new UserFacingException("The doctor to hand the patients over to could not be found.");
                    var patients = Rows(cn, tx, "SELECT PatientId FROM dbo.PAT_CareTeam WHERE DoctorEmployeeId = @e AND IsActive = 1", "@e", employeeId).Select(r => ToInt(r["PatientId"])).ToList();
                    string name = Convert.ToString(Scalar(cn, tx, "SELECT FullName FROM dbo.EMP_Employee WHERE EmployeeId = @e", "@e", employeeId));
                    foreach (int pid in patients)
                        if (PutOnTeam(cn, tx, pid, to, "Shared", admin.EmployeeId, "Taken over from " + name + " (withdrawn)")) handed++;
                    if (handed > 0)
                        SendIn(cn, tx, admin.EmployeeId, new[] { to }, "Patients handed over to you",
                               handed + " patient(s) of " + name + " have been handed over to you by the administrator. You will find them under Care Team > My Patients.", null, "CareUpdate");
                }
                tx.Commit();
                Log(admin.EmployeeId, "AdminWithdraw", null, employeeId, Clip((reason ?? "") + (handed > 0 ? " | " + handed + " patient(s) handed over" : ""), 250));
            }
            InvalidateCallers();
            InvalidateTeams();
        }

        public void Reactivate(CallerInfo admin, int userId)
        {
            var target = Rows("SELECT UserName, EmployeeId FROM dbo.RBAC_User WHERE UserId = @u", "@u", userId).FirstOrDefault();
            if (target == null) throw new UserFacingException("That login could not be found.");
            Execute("UPDATE dbo.RBAC_User SET IsActive = 1, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE UserId = @u", "@by", admin.EmployeeId, "@u", userId);
            Execute("UPDATE dbo.EMP_Employee SET IsActive = 1, ModifiedBy = @by, ModifiedOn = GETDATE() WHERE EmployeeId = @e", "@by", admin.EmployeeId, "@e", ToInt(target["EmployeeId"]));
            Log(admin.EmployeeId, "AdminReactivate", null, ToInt(target["EmployeeId"]), Convert.ToString(target["UserName"]));
            InvalidateCallers();
        }
    }
}
