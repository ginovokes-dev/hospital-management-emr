using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DanpheEMR.CareTeam
{
    public class RegisterPatientInput
    {
        public string Salutation { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string PhoneNumber { get; set; }
        public string Address { get; set; }
        public string Email { get; set; }
        public string BloodGroup { get; set; }
    }

    /// <summary>my patients, find a patient, add myself / share / leave, register a patient, start a consultation</summary>
    public partial class CareTeamStore
    {
        // ---------------------------------------------------------------------------------------------------------------------
        // search helpers
        // ---------------------------------------------------------------------------------------------------------------------
        private const string FullNameSql = "LTRIM(RTRIM(p.FirstName + ' ' + ISNULL(p.MiddleName + ' ', '') + ISNULL(p.LastName, '')))";

        private static string EscapeLike(string s)
        {
            return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        }

        /// <summary>every word typed must appear in the name, the patient code or the phone number</summary>
        private static string SearchClause(string search, List<object> args)
        {
            if (string.IsNullOrWhiteSpace(search)) return "";
            var sb = new System.Text.StringBuilder();
            int i = 0;
            foreach (string term in search.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Take(6))
            {
                string pn = "@s" + (i++);
                args.Add(pn);
                args.Add("%" + EscapeLike(term) + "%");
                sb.Append(" AND (" + FullNameSql + " LIKE " + pn + " ESCAPE '\\' OR p.PatientCode LIKE " + pn + " ESCAPE '\\' OR p.PhoneNumber LIKE " + pn + " ESCAPE '\\')");
            }
            return sb.ToString();
        }

        private Dictionary<int, List<Dictionary<string, object>>> TeamsFor(IEnumerable<int> patientIds)
        {
            var map = new Dictionary<int, List<Dictionary<string, object>>>();
            var ids = patientIds.Distinct().ToList();
            if (ids.Count == 0) return map;
            var rows = Rows(@"SELECT c.PatientId, e.EmployeeId, e.FullName, e.Speciality, e.IsActive AS DoctorActive, c.Relationship, c.AddedOn
                                FROM dbo.PAT_CareTeam c
                                JOIN dbo.EMP_Employee e ON e.EmployeeId = c.DoctorEmployeeId
                               WHERE c.IsActive = 1 AND c.PatientId IN (" + IntList(ids) + @")
                               ORDER BY c.AddedOn");
            foreach (var r in rows)
            {
                int pid = ToInt(r["PatientId"]);
                List<Dictionary<string, object>> l;
                if (!map.TryGetValue(pid, out l)) map[pid] = l = new List<Dictionary<string, object>>();
                l.Add(r);
            }
            return map;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // my patients / find a patient
        // ---------------------------------------------------------------------------------------------------------------------
        public List<Dictionary<string, object>> MyPatients(int employeeId, string search, int take)
        {
            var args = new List<object> { "@d", employeeId, "@take", Math.Min(Math.Max(take, 1), 500) };
            string where = SearchClause(search, args);
            var rows = Rows(@"SELECT TOP (@take) p.PatientId, p.PatientCode, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.Age, p.DateOfBirth,
                                     p.PhoneNumber, p.Address, c.Relationship, c.AddedOn, c.Note, ab.FullName AS AddedByName,
                                     lv.PatientVisitId AS LastVisitId, lv.VisitDate AS LastVisitDate
                                FROM dbo.PAT_CareTeam c
                                JOIN dbo.PAT_Patient p ON p.PatientId = c.PatientId
                                LEFT JOIN dbo.EMP_Employee ab ON ab.EmployeeId = c.AddedByEmployeeId
                                OUTER APPLY (SELECT TOP 1 v.PatientVisitId, v.VisitDate FROM dbo.PAT_PatientVisits v
                                              WHERE v.PatientId = p.PatientId ORDER BY v.VisitDate DESC, v.PatientVisitId DESC) lv
                               WHERE c.DoctorEmployeeId = @d AND c.IsActive = 1 AND p.IsActive = 1" + where + @"
                               ORDER BY ISNULL(lv.VisitDate, c.AddedOn) DESC, p.PatientId DESC", args.ToArray());
            var teams = TeamsFor(rows.Select(r => ToInt(r["PatientId"])));
            foreach (var r in rows)
            {
                r["Name"] = NameOf(r);
                List<Dictionary<string, object>> t;
                r["Team"] = teams.TryGetValue(ToInt(r["PatientId"]), out t) ? t.Where(x => ToInt(x["EmployeeId"]) != employeeId).ToList() : new List<Dictionary<string, object>>();
            }
            return rows;
        }

        /// <summary>
        /// Anyone with a doctor login may look a patient up by name / code / phone to see WHO the patient is under.
        /// Only name, code, sex and age are shown - the record itself opens after "Add to my care". Every search is logged.
        /// </summary>
        public List<Dictionary<string, object>> FindPatients(int employeeId, string search, int take)
        {
            var args = new List<object> { "@take", Math.Min(Math.Max(take, 1), 50) };
            string where = SearchClause(search, args);
            var rows = Rows(@"SELECT TOP (@take) p.PatientId, p.PatientCode, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.Age, p.DateOfBirth
                                FROM dbo.PAT_Patient p
                               WHERE p.IsActive = 1" + where + @"
                               ORDER BY p.FirstName, p.LastName, p.PatientId", args.ToArray());
            var teams = TeamsFor(rows.Select(r => ToInt(r["PatientId"])));
            foreach (var r in rows)
            {
                r["Name"] = NameOf(r);
                List<Dictionary<string, object>> t;
                t = teams.TryGetValue(ToInt(r["PatientId"]), out t) ? t : new List<Dictionary<string, object>>();
                r["Team"] = t;
                r["IsMine"] = t.Any(x => ToInt(x["EmployeeId"]) == employeeId);
            }
            Log(employeeId, "Search", null, null, "\"" + (search ?? "").Trim() + "\" -> " + rows.Count + " match(es)");
            return rows;
        }

        public List<Dictionary<string, object>> TeamOf(int patientId)
        {
            List<Dictionary<string, object>> t;
            return TeamsFor(new[] { patientId }).TryGetValue(patientId, out t) ? t : new List<Dictionary<string, object>>();
        }

        public Dictionary<string, object> PatientBasics(int patientId)
        {
            var r = Rows(@"SELECT PatientId, PatientCode, Salutation, FirstName, MiddleName, LastName, ShortName, Gender, Age, DateOfBirth, PhoneNumber, Address, Email, EMPI
                             FROM dbo.PAT_Patient WHERE PatientId = @p", "@p", patientId).FirstOrDefault();
            if (r != null) r["Name"] = NameOf(r);
            return r;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // staff directory (share / message pickers)
        // ---------------------------------------------------------------------------------------------------------------------
        /// <param name="doctorsOnly">true: only doctor logins (people a patient can be shared with); false: every active login (messages)</param>
        public List<Dictionary<string, object>> Directory(int excludeEmployeeId, bool doctorsOnly)
        {
            return Rows(@"SELECT e.EmployeeId, e.FullName AS Name, e.Speciality, e.Email, MAX(r.RoleName) AS RoleName
                            FROM dbo.RBAC_User u
                            JOIN dbo.EMP_Employee e ON e.EmployeeId = u.EmployeeId
                            JOIN dbo.RBAC_MAP_UserRole m ON m.UserId = u.UserId AND ISNULL(m.IsActive, 1) = 1
                            JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId AND ISNULL(r.IsActive, 1) = 1
                           WHERE u.IsActive = 1 AND e.IsActive = 1 AND e.EmployeeId <> @me
                             AND (@doctorsOnly = 0 OR ISNULL(r.ConfineToCareTeam, 0) = 1)
                           GROUP BY e.EmployeeId, e.FullName, e.Speciality, e.Email
                           ORDER BY e.FullName", "@me", excludeEmployeeId, "@doctorsOnly", doctorsOnly ? 1 : 0);
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // add myself / share / leave
        // ---------------------------------------------------------------------------------------------------------------------
        private void EnsureActivePatient(int patientId)
        {
            if (Scalar("SELECT TOP 1 1 FROM dbo.PAT_Patient WHERE PatientId = @p AND IsActive = 1", "@p", patientId) == null)
                throw new UserFacingException("That patient could not be found.");
        }

        private static string DoctorName(CallerInfo c)
        {
            return string.IsNullOrWhiteSpace(c.DisplayName) ? c.UserName : c.DisplayName;
        }

        /// <summary>puts a doctor on a patient's team (or brings back one who had left). Returns true when something changed.</summary>
        private bool PutOnTeam(SqlConnection cn, SqlTransaction tx, int patientId, int doctorEmployeeId, string relationship, int addedBy, string note)
        {
            var existing = Rows(cn, tx, "SELECT IsActive FROM dbo.PAT_CareTeam WHERE PatientId = @p AND DoctorEmployeeId = @d", "@p", patientId, "@d", doctorEmployeeId).FirstOrDefault();
            if (existing == null)
            {
                Execute(cn, tx, @"INSERT dbo.PAT_CareTeam (PatientId, DoctorEmployeeId, Relationship, AddedByEmployeeId, Note)
                                  VALUES (@p, @d, @rel, @by, @note)", "@p", patientId, "@d", doctorEmployeeId, "@rel", relationship, "@by", addedBy, "@note", Clip(note, 500));
                return true;
            }
            if (!ToBool(existing["IsActive"]))
            {
                Execute(cn, tx, @"UPDATE dbo.PAT_CareTeam SET IsActive = 1, EndedOn = NULL, EndedByEmployeeId = NULL, Relationship = @rel,
                                         AddedByEmployeeId = @by, AddedOn = GETDATE(), Note = @note
                                   WHERE PatientId = @p AND DoctorEmployeeId = @d", "@p", patientId, "@d", doctorEmployeeId, "@rel", relationship, "@by", addedBy, "@note", Clip(note, 500));
                return true;
            }
            return false;
        }

        private static string Clip(string s, int max)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>a doctor adds themselves to a patient they found by name</summary>
        public bool AddSelf(CallerInfo me, int patientId, string note)
        {
            EnsureActivePatient(patientId);
            bool changed;
            var others = new List<int>();
            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                others = Rows(cn, tx, "SELECT DoctorEmployeeId FROM dbo.PAT_CareTeam WHERE PatientId = @p AND IsActive = 1 AND DoctorEmployeeId <> @d", "@p", patientId, "@d", me.EmployeeId)
                            .Select(r => ToInt(r["DoctorEmployeeId"])).ToList();
                changed = PutOnTeam(cn, tx, patientId, me.EmployeeId, "Self", me.EmployeeId, note);
                if (changed && others.Count > 0)
                {
                    string label = PatientLabelIn(cn, tx, patientId);
                    SendIn(cn, tx, me.EmployeeId, others, "Care update: " + label,
                           DoctorName(me) + " has added themselves to the care team of " + label + "." + (string.IsNullOrWhiteSpace(note) ? "" : "\n\nNote: " + note.Trim()),
                           patientId, "CareUpdate");
                }
                tx.Commit();
            }
            if (changed) Log(me.EmployeeId, "AddSelf", patientId, null, Clip(note, 250));
            InvalidateTeams();
            return changed;
        }

        /// <summary>a doctor on the team hands the patient over to / shares the patient with another doctor</summary>
        public bool Share(CallerInfo me, int patientId, int toEmployeeId, string message)
        {
            if (toEmployeeId == me.EmployeeId) throw new UserFacingException("You are already on this patient's care team.");
            if (!IsInCareTeam(me.EmployeeId, patientId)) throw new UserFacingException("Only a doctor on this patient's care team can share the patient.");
            EnsureActivePatient(patientId);
            var target = Directory(me.EmployeeId, true).FirstOrDefault(d => ToInt(d["EmployeeId"]) == toEmployeeId);
            if (target == null) throw new UserFacingException("That doctor could not be found (or has been withdrawn).");

            bool changed;
            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                changed = PutOnTeam(cn, tx, patientId, toEmployeeId, "Shared", me.EmployeeId, message);
                string label = PatientLabelIn(cn, tx, patientId);
                SendIn(cn, tx, me.EmployeeId, new[] { toEmployeeId }, "Patient shared with you: " + label,
                       DoctorName(me) + " has shared " + label + " with you. The patient is now on your list under Care Team > My Patients." +
                       (string.IsNullOrWhiteSpace(message) ? "" : "\n\n" + message.Trim()), patientId, "PatientShare");
                tx.Commit();
            }
            Log(me.EmployeeId, "Share", patientId, toEmployeeId, Clip(message, 250));
            InvalidateTeams();
            return changed;
        }

        public void RemoveSelf(CallerInfo me, int patientId)
        {
            int n;
            var others = new List<int>();
            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                n = Execute(cn, tx, @"UPDATE dbo.PAT_CareTeam SET IsActive = 0, EndedOn = GETDATE(), EndedByEmployeeId = @d
                                       WHERE PatientId = @p AND DoctorEmployeeId = @d AND IsActive = 1", "@p", patientId, "@d", me.EmployeeId);
                if (n > 0)
                {
                    others = Rows(cn, tx, "SELECT DoctorEmployeeId FROM dbo.PAT_CareTeam WHERE PatientId = @p AND IsActive = 1", "@p", patientId).Select(r => ToInt(r["DoctorEmployeeId"])).ToList();
                    if (others.Count > 0)
                    {
                        string label = PatientLabelIn(cn, tx, patientId);
                        SendIn(cn, tx, me.EmployeeId, others, "Care update: " + label, DoctorName(me) + " is no longer on the care team of " + label + ".", patientId, "CareUpdate");
                    }
                }
                tx.Commit();
            }
            if (n > 0) Log(me.EmployeeId, "RemoveSelf", patientId, null, null);
            InvalidateTeams();
        }

        private string PatientLabelIn(SqlConnection cn, SqlTransaction tx, int patientId)
        {
            var r = Rows(cn, tx, "SELECT PatientCode, FirstName, MiddleName, LastName FROM dbo.PAT_Patient WHERE PatientId = @p", "@p", patientId).FirstOrDefault();
            return r == null ? "patient #" + patientId : NameOf(r) + " (" + Convert.ToString(r["PatientCode"]) + ")";
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // register a patient / start a consultation
        // ---------------------------------------------------------------------------------------------------------------------
        private static string AgeText(DateTime dob)
        {
            DateTime now = DateTime.Now;
            int years = now.Year - dob.Year;
            if (dob.Date > now.Date.AddYears(-years)) years--;
            if (years >= 1) return years + "Y";
            int months = (now.Year - dob.Year) * 12 + now.Month - dob.Month;
            if (now.Day < dob.Day) months--;
            if (months >= 1) return months + "M";
            return Math.Max((now.Date - dob.Date).Days, 0) + "D";
        }

        /// <summary>a hospital setting from CORE_CFG_Parameters (group = null: any group)</summary>
        private string ParameterValue(SqlConnection cn, SqlTransaction tx, string group, string name)
        {
            var o = Scalar(cn, tx, "SELECT TOP 1 ParameterValue FROM dbo.CORE_CFG_Parameters WHERE (@g IS NULL OR ParameterGroupName = @g) AND ParameterName = @n", "@g", group, "@n", name);
            return o == null ? null : Convert.ToString(o);
        }

        /// <summary>creates the patient the way the Appointment screen does (same numbering rules) and puts the registering doctor on the team</summary>
        public Dictionary<string, object> RegisterPatient(CallerInfo me, RegisterPatientInput d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.FirstName)) throw new UserFacingException("Please enter the patient's first name.");
            if (string.IsNullOrWhiteSpace(d.Gender)) throw new UserFacingException("Please choose the patient's gender.");
            if (d.DateOfBirth == null) throw new UserFacingException("Please enter the patient's date of birth.");
            if (d.DateOfBirth.Value.Date > DateTime.Today) throw new UserFacingException("The date of birth cannot be in the future.");
            if (d.DateOfBirth.Value.Year < 1880) throw new UserFacingException("Please check the date of birth.");

            string first = Clip(d.FirstName, 100), middle = Clip(d.MiddleName, 100), last = Clip(d.LastName, 100);
            string gender = Clip(d.Gender, 10);
            string shortName = ((first ?? "") + " " + (last ?? "")).Trim();

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var cn = Open())
                    using (var tx = cn.BeginTransaction())
                    {
                        // country / region defaults come from the hospital's own settings
                        int countryId = 0, subDivId = 0;
                        string subName = null;
                        try
                        {
                            var jc = JObject.Parse(ParameterValue(cn, tx, "Common", "DefaultCountry") ?? "{}");
                            int.TryParse((string)jc["CountryId"], out countryId);
                            var js = JObject.Parse(ParameterValue(cn, tx, "Common", "DefaultCountrySubDivision") ?? "{}");
                            int.TryParse((string)js["CountrySubDivisionId"], out subDivId);
                            subName = (string)js["CountrySubDivisionName"];
                        }
                        catch (Exception) { }
                        if (countryId == 0) countryId = ToInt(Scalar(cn, tx, "SELECT TOP 1 CountryId FROM dbo.MST_Country ORDER BY CountryId"));
                        if (countryId == 0) countryId = 1;

                        int patNo = ToInt(Scalar(cn, tx, "SELECT ISNULL(MAX(PatientNo), 0) + 1 FROM dbo.PAT_Patient WITH (UPDLOCK, HOLDLOCK)"));
                        string format = ParameterValue(cn, tx, "Patient", "PatientCodeFormat") ?? "YYMM-PatNum";
                        string code;
                        if (format == "HospCode-PatNum") code = (ParameterValue(cn, tx, null, "HospitalCode") ?? "") + patNo;
                        else if (format == "PatNum") code = patNo.ToString(CultureInfo.InvariantCulture);
                        else code = DateTime.Now.ToString("yyMM", CultureInfo.InvariantCulture) + patNo.ToString("D6", CultureInfo.InvariantCulture);
                        if (code.Length > 10) code = code.Substring(code.Length - 10);

                        string initials = ((first ?? "X").Substring(0, 1) + (string.IsNullOrEmpty(middle) ? "X" : middle.Substring(0, 1)) + (string.IsNullOrEmpty(last) ? "X" : last.Substring(0, 1))).ToUpperInvariant();
                        string region = new string((subName ?? "XXX").Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant().PadRight(3, 'X');
                        string empi = region + d.DateOfBirth.Value.ToString("ddMMyy", CultureInfo.InvariantCulture) + initials + new Random().Next(1000, 10000);

                        var idObj = Scalar(cn, tx, @"INSERT dbo.PAT_Patient
                                (PatientCode, PatientNo, Salutation, FirstName, MiddleName, LastName, Gender, Age, DateOfBirth, PhoneNumber, Address, Email, BloodGroup,
                                 CountryId, CountrySubDivisionId, EMPI, PhoneAcceptsText, IsOutdoorPat, CreatedOn, CreatedBy, IsActive, ShortName,
                                 Ins_HasInsurance, Ins_InsuranceBalance, IsSSUPatient, SSU_IsActive, IsVaccinationPatient, IsVaccinationActive, IsDobVerified)
                              VALUES (@code, @no, @sal, @fn, @mn, @ln, @g, @age, @dob, @ph, @addr, @mail, @bg,
                                      @country, @sub, @empi, 0, 0, GETDATE(), @by, 1, @short,
                                      0, 0, 0, 0, 0, 0, 0);
                              SELECT CAST(SCOPE_IDENTITY() AS int);",
                            "@code", code, "@no", patNo, "@sal", Clip(d.Salutation, 10), "@fn", first, "@mn", middle, "@ln", last, "@g", gender, "@age", AgeText(d.DateOfBirth.Value),
                            "@dob", d.DateOfBirth.Value.Date, "@ph", Clip(d.PhoneNumber, 20), "@addr", Clip(d.Address, 100), "@mail", Clip(d.Email, 50), "@bg", Clip(d.BloodGroup, 20),
                            "@country", countryId, "@sub", subDivId > 0 ? (object)subDivId : null, "@empi", empi, "@by", me.EmployeeId, "@short", Clip(shortName, 100));
                        int patientId = ToInt(idObj);

                        // the trigger already added a doctor who is an "appointment" employee; make sure of it for everyone else too
                        PutOnTeam(cn, tx, patientId, me.EmployeeId, "Primary", me.EmployeeId, "Registered by this doctor");
                        tx.Commit();

                        Log(me.EmployeeId, "Register", patientId, null, code);
                        InvalidateTeams();
                        var res = PatientBasics(patientId);
                        return res;
                    }
                }
                catch (SqlException ex)
                {
                    // 2627 / 2601: the number was taken by someone else a moment ago - try again with the next one
                    if ((ex.Number == 2627 || ex.Number == 2601) && attempt < 4) continue;
                    throw;
                }
            }
            throw new UserFacingException("The patient could not be saved. Please try again.");
        }

        /// <summary>
        /// The patient record screens work on a "visit". Returns the visit to open for this doctor: today's visit with them if there is one,
        /// else their most recent one, else a new consultation for today. forceNewToday creates today's visit when there is none today.
        /// </summary>
        public Dictionary<string, object> VisitForChart(CallerInfo me, int patientId, bool forceNewToday)
        {
            if (!IsInCareTeam(me.EmployeeId, patientId)) throw new UserFacingException("This patient is not under your care.");
            EnsureActivePatient(patientId);

            var visits = Rows(@"SELECT TOP 1 PatientVisitId, VisitCode, VisitDate, VisitStatus, ConcludeDate
                                  FROM dbo.PAT_PatientVisits
                                 WHERE PatientId = @p AND PerformerId = @d AND IsActive = 1 AND VisitType = 'outpatient'
                                   AND BillingStatus <> 'returned' AND BillingStatus <> 'cancel'
                                 ORDER BY VisitDate DESC, PatientVisitId DESC", "@p", patientId, "@d", me.EmployeeId);
            var last = visits.FirstOrDefault();
            bool haveToday = last != null && ((DateTime)last["VisitDate"]).Date == DateTime.Today && last["ConcludeDate"] == null;
            if (last != null && (haveToday || !forceNewToday))
            {
                last["Created"] = false;
                return last;
            }
            return CreateVisit(me, patientId);
        }

        private Dictionary<string, object> CreateVisit(CallerInfo me, int patientId)
        {
            var emp = Rows("SELECT FullName, DepartmentId FROM dbo.EMP_Employee WHERE EmployeeId = @e", "@e", me.EmployeeId).FirstOrDefault();
            int deptId = emp == null ? 0 : ToInt(emp["DepartmentId"]);
            if (deptId == 0) deptId = ToInt(Scalar("SELECT TOP 1 DepartmentId FROM dbo.MST_Department WHERE IsAppointmentApplicable = 1 AND IsActive = 1 ORDER BY DepartmentId"));
            if (deptId == 0) throw new UserFacingException("Your account has no department yet. Please ask the administrator to set your speciality.");

            // the scheme / price category new visits use (the hospital's default)
            var scheme = Rows(@"SELECT TOP 1 SchemeId, ISNULL(DefaultPriceCategoryId, 0) AS PriceCategoryId FROM dbo.BIL_CFG_Scheme
                                 WHERE IsActive = 1 ORDER BY ISNULL(IsSystemDefault, 0) DESC, SchemeId").FirstOrDefault();
            int schemeId = scheme == null ? 1 : ToInt(scheme["SchemeId"]);
            int priceCat = scheme == null ? 1 : ToInt(scheme["PriceCategoryId"]);
            if (priceCat == 0) priceCat = ToInt(Scalar("SELECT TOP 1 PriceCategoryId FROM dbo.BIL_CFG_PriceCategory WHERE IsActive = 1 ORDER BY IsDefault DESC, PriceCategoryId")) ;
            if (priceCat == 0) priceCat = 1;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                string code = DanpheEMR.Controllers.VisitBL.CreateNewPatientVisitCode("outpatient", _cs);
                try
                {
                    int visitId = ToInt(Scalar(@"INSERT dbo.PAT_PatientVisits
                            (PatientId, VisitDate, PerformerName, VisitType, VisitStatus, VisitTime, VisitCode, PerformerId, BillingStatus, AppointmentType,
                             IsVisitContinued, CreatedOn, CreatedBy, IsActive, IsSignedVisitSummary, DepartmentId, IsTriaged, Ins_HasInsurance,
                             PriceCategoryId, SchemeId, TicketCharge, IsFreeVisit)
                          VALUES (@p, @today, @pn, 'outpatient', 'initiated', CONVERT(time(0), GETDATE()), @code, @d, 'free', 'New',
                                  0, GETDATE(), @d, 1, 0, @dept, 0, 0,
                                  @pc, @sc, 0, 1);
                          SELECT CAST(SCOPE_IDENTITY() AS int);",
                        "@p", patientId, "@today", DateTime.Today, "@pn", emp == null ? me.DisplayName : Convert.ToString(emp["FullName"]), "@code", code, "@d", me.EmployeeId,
                        "@dept", deptId, "@pc", priceCat, "@sc", schemeId));
                    try { Execute("EXEC dbo.SP_VISIT_SetNGetQueueNo @VisitId = @v", "@v", visitId); } catch (Exception) { /* queue numbers are optional */ }
                    Log(me.EmployeeId, "StartVisit", patientId, null, code);
                    return new Dictionary<string, object> { { "PatientVisitId", visitId }, { "VisitCode", code }, { "VisitDate", DateTime.Today }, { "Created", true } };
                }
                catch (SqlException ex)
                {
                    if ((ex.Number == 2627 || ex.Number == 2601) && attempt < 4) continue;
                    throw;
                }
            }
            throw new UserFacingException("The consultation could not be started. Please try again.");
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // small numbers for the badges
        // ---------------------------------------------------------------------------------------------------------------------
        public Dictionary<string, object> Summary(int employeeId)
        {
            return new Dictionary<string, object>
            {
                { "Patients", ToInt(Scalar("SELECT COUNT(*) FROM dbo.PAT_CareTeam c JOIN dbo.PAT_Patient p ON p.PatientId = c.PatientId WHERE c.DoctorEmployeeId = @d AND c.IsActive = 1 AND p.IsActive = 1", "@d", employeeId)) },
                { "UnreadMessages", UnreadCount(employeeId) }
            };
        }
    }
}
