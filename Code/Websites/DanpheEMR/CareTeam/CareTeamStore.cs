using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

namespace DanpheEMR.CareTeam
{
    /// <summary>
    /// A person the application recognises after login, as the access rules see them (database state, cached for a few seconds).
    /// </summary>
    public class CallerInfo
    {
        public int UserId { get; set; }
        public int EmployeeId { get; set; }
        public string UserName { get; set; }
        public string DisplayName { get; set; }
        /// <summary>login switched on AND employee record switched on</summary>
        public bool IsActive { get; set; }
        /// <summary>holds a role flagged IsSysAdmin (the administrator)</summary>
        public bool IsSuperAdmin { get; set; }
        /// <summary>holds a role flagged ConfineToCareTeam (doctors): clinical screens only, own patients only</summary>
        public bool Confined { get; set; }
        public List<string> RoleNames { get; set; } = new List<string>();
        internal DateTime LoadedAt { get; set; }
    }

    /// <summary>a problem the user can fix (bad input, not allowed ...) - the message is shown to them as it is</summary>
    public class UserFacingException : Exception
    {
        public UserFacingException(string message) : base(message) { }
    }

    /// <summary>
    /// All database access of the care-team / messaging / doctor-administration features, plus the lookups the access rules need.
    /// Plain ADO.NET with parameters only (ids that are put into IN (...) lists are ints).
    /// </summary>
    public partial class CareTeamStore
    {
        private readonly string _cs;
        private static readonly TimeSpan CallerTtl = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan TeamTtl = TimeSpan.FromSeconds(10);

        private class TeamCacheEntry
        {
            public HashSet<int> PatientIds = new HashSet<int>();
            public DateTime LoadedAt;
        }

        private readonly ConcurrentDictionary<int, CallerInfo> _callers = new ConcurrentDictionary<int, CallerInfo>();
        private readonly ConcurrentDictionary<int, TeamCacheEntry> _teams = new ConcurrentDictionary<int, TeamCacheEntry>();
        private readonly ConcurrentDictionary<int, int> _visitToPatient = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<string, DateTime> _deniedLogged = new ConcurrentDictionary<string, DateTime>();

        public CareTeamStore(string connectionString)
        {
            _cs = connectionString;
        }

        public string ConnectionString { get { return _cs; } }

        // ---------------------------------------------------------------------------------------------------------------------
        // small ADO.NET helpers
        // ---------------------------------------------------------------------------------------------------------------------
        internal SqlConnection Open()
        {
            var cn = new SqlConnection(_cs);
            cn.Open();
            return cn;
        }

        private static void AddParams(SqlCommand cmd, object[] nameValuePairs)
        {
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
            {
                object v = nameValuePairs[i + 1];
                cmd.Parameters.AddWithValue((string)nameValuePairs[i], v ?? DBNull.Value);
            }
        }

        /// <summary>runs a query, returns the rows as dictionaries (DBNull becomes null)</summary>
        internal List<Dictionary<string, object>> Rows(string sql, params object[] nameValuePairs)
        {
            using (var cn = Open())
            {
                return Rows(cn, null, sql, nameValuePairs);
            }
        }

        internal List<Dictionary<string, object>> Rows(SqlConnection cn, SqlTransaction tx, string sql, params object[] nameValuePairs)
        {
            var list = new List<Dictionary<string, object>>();
            using (var cmd = new SqlCommand(sql, cn, tx))
            {
                AddParams(cmd, nameValuePairs);
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        var row = new Dictionary<string, object>(rd.FieldCount);
                        for (int i = 0; i < rd.FieldCount; i++)
                        {
                            object v = rd.GetValue(i);
                            row[rd.GetName(i)] = v == DBNull.Value ? null : v;
                        }
                        list.Add(row);
                    }
                }
            }
            return list;
        }

        internal object Scalar(string sql, params object[] nameValuePairs)
        {
            using (var cn = Open())
            {
                return Scalar(cn, null, sql, nameValuePairs);
            }
        }

        internal object Scalar(SqlConnection cn, SqlTransaction tx, string sql, params object[] nameValuePairs)
        {
            using (var cmd = new SqlCommand(sql, cn, tx))
            {
                AddParams(cmd, nameValuePairs);
                object o = cmd.ExecuteScalar();
                return o == DBNull.Value ? null : o;
            }
        }

        internal int Execute(string sql, params object[] nameValuePairs)
        {
            using (var cn = Open())
            {
                return Execute(cn, null, sql, nameValuePairs);
            }
        }

        internal int Execute(SqlConnection cn, SqlTransaction tx, string sql, params object[] nameValuePairs)
        {
            using (var cmd = new SqlCommand(sql, cn, tx))
            {
                AddParams(cmd, nameValuePairs);
                return cmd.ExecuteNonQuery();
            }
        }

        internal static int ToInt(object o)
        {
            return o == null ? 0 : Convert.ToInt32(o);
        }

        internal static bool ToBool(object o)
        {
            return o != null && Convert.ToBoolean(o);
        }

        /// <summary>"1,2,3" for an IN (...) list - ints only, so nothing can be injected</summary>
        internal static string IntList(IEnumerable<int> ids)
        {
            return string.Join(",", ids.Distinct().Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // who is calling (roles, active flags)
        // ---------------------------------------------------------------------------------------------------------------------
        public CallerInfo GetCaller(int userId)
        {
            CallerInfo c;
            if (_callers.TryGetValue(userId, out c) && DateTime.UtcNow - c.LoadedAt < CallerTtl)
                return c;

            var u = Rows(@"SELECT u.UserId, u.EmployeeId, u.UserName, u.IsActive AS LoginActive, e.IsActive AS EmployeeActive,
                                  LTRIM(RTRIM(ISNULL(e.FullName, u.UserName))) AS DisplayName
                             FROM dbo.RBAC_User u
                             LEFT JOIN dbo.EMP_Employee e ON e.EmployeeId = u.EmployeeId
                            WHERE u.UserId = @u", "@u", userId).FirstOrDefault();
            if (u == null)
            {
                _callers.TryRemove(userId, out c);
                return null;
            }

            var roles = Rows(@"SELECT r.RoleId, r.RoleName, ISNULL(r.IsSysAdmin, 0) AS IsSys, ISNULL(r.ConfineToCareTeam, 0) AS Confine
                                 FROM dbo.RBAC_MAP_UserRole m
                                 JOIN dbo.RBAC_Role r ON r.RoleId = m.RoleId
                                WHERE m.UserId = @u AND ISNULL(m.IsActive, 1) = 1 AND ISNULL(r.IsActive, 1) = 1", "@u", userId);

            c = new CallerInfo
            {
                UserId = userId,
                EmployeeId = ToInt(u["EmployeeId"]),
                UserName = Convert.ToString(u["UserName"]),
                DisplayName = Convert.ToString(u["DisplayName"]),
                IsActive = ToBool(u["LoginActive"]) && (u["EmployeeActive"] == null || ToBool(u["EmployeeActive"])),
                IsSuperAdmin = roles.Any(r => ToBool(r["IsSys"])),
                RoleNames = roles.Select(r => Convert.ToString(r["RoleName"])).ToList(),
                LoadedAt = DateTime.UtcNow
            };
            c.Confined = !c.IsSuperAdmin && roles.Any(r => ToBool(r["Confine"]));
            _callers[userId] = c;
            return c;
        }

        public void InvalidateCallers()
        {
            _callers.Clear();
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // care team membership
        // ---------------------------------------------------------------------------------------------------------------------
        /// <summary>patients currently under the doctor's care (a copy). fresh = skip the few-seconds cache</summary>
        public HashSet<int> GetCareTeamPatientIds(int employeeId, bool fresh)
        {
            TeamCacheEntry e;
            if (!fresh && _teams.TryGetValue(employeeId, out e) && DateTime.UtcNow - e.LoadedAt < TeamTtl)
            {
                lock (e.PatientIds) { return new HashSet<int>(e.PatientIds); }
            }

            var ids = new HashSet<int>();
            foreach (var r in Rows("SELECT PatientId FROM dbo.PAT_CareTeam WHERE DoctorEmployeeId = @d AND IsActive = 1", "@d", employeeId))
                ids.Add(ToInt(r["PatientId"]));
            _teams[employeeId] = new TeamCacheEntry { PatientIds = ids, LoadedAt = DateTime.UtcNow };
            return new HashSet<int>(ids);
        }

        public bool IsInCareTeam(int employeeId, int patientId)
        {
            if (patientId <= 0) return true;   // "no patient" is not a patient
            TeamCacheEntry e;
            if (_teams.TryGetValue(employeeId, out e) && DateTime.UtcNow - e.LoadedAt < TeamTtl)
            {
                lock (e.PatientIds) { if (e.PatientIds.Contains(patientId)) return true; }
            }
            // not in the (possibly stale) cache: ask the database before saying no - the doctor may have just registered the patient
            object o = Scalar("SELECT TOP 1 1 FROM dbo.PAT_CareTeam WHERE DoctorEmployeeId = @d AND PatientId = @p AND IsActive = 1", "@d", employeeId, "@p", patientId);
            if (o != null)
            {
                if (_teams.TryGetValue(employeeId, out e)) lock (e.PatientIds) { e.PatientIds.Add(patientId); }
                return true;
            }
            return false;
        }

        public void InvalidateTeams()
        {
            _teams.Clear();
        }

        /// <summary>visit id -> patient id (0 when the visit does not exist). The mapping never changes, so it is remembered.</summary>
        public Dictionary<int, int> GetPatientIdsForVisits(IEnumerable<int> visitIds)
        {
            var result = new Dictionary<int, int>();
            var todo = new List<int>();
            foreach (int v in visitIds.Where(x => x > 0).Distinct())
            {
                int p;
                if (_visitToPatient.TryGetValue(v, out p)) result[v] = p; else todo.Add(v);
            }
            for (int i = 0; i < todo.Count; i += 500)
            {
                var batch = todo.Skip(i).Take(500).ToList();
                foreach (var r in Rows("SELECT PatientVisitId, PatientId FROM dbo.PAT_PatientVisits WHERE PatientVisitId IN (" + IntList(batch) + ")"))
                {
                    int v = ToInt(r["PatientVisitId"]), p = ToInt(r["PatientId"]);
                    result[v] = p;
                    if (_visitToPatient.Count > 200000) _visitToPatient.Clear();
                    _visitToPatient[v] = p;
                }
                foreach (int v in batch) if (!result.ContainsKey(v)) result[v] = 0;
            }
            return result;
        }

        /// <summary>
        /// record number -> patient for rows of one table ("Table.Column", from the fixed list in AccessRules.EntityColumn - never from the
        /// request). Numbers that do not exist are simply missing from the answer.
        /// </summary>
        public Dictionary<int, int> GetPatientIdsForRecords(string tableAndColumn, IEnumerable<int> ids)
        {
            var result = new Dictionary<int, int>();
            string[] parts = (tableAndColumn ?? "").Split('.');
            if (parts.Length != 2) return result;
            var todo = ids.Where(x => x > 0).Distinct().ToList();
            for (int i = 0; i < todo.Count; i += 500)
            {
                var batch = todo.Skip(i).Take(500).ToList();
                foreach (var r in Rows("SELECT [" + parts[1] + "] AS RecordId, PatientId FROM dbo.[" + parts[0] + "] WHERE [" + parts[1] + "] IN (" + IntList(batch) + ")"))
                    result[ToInt(r["RecordId"])] = ToInt(r["PatientId"]);
            }
            return result;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // audit trail
        // ---------------------------------------------------------------------------------------------------------------------
        public void Log(int actorEmployeeId, string action, int? patientId, int? targetEmployeeId, string detail)
        {
            try
            {
                if (detail != null && detail.Length > 300) detail = detail.Substring(0, 300);
                Execute(@"INSERT dbo.PAT_CareTeamLog (ActorEmployeeId, Action, PatientId, TargetEmployeeId, Detail)
                          VALUES (@a, @act, @p, @t, @d)", "@a", actorEmployeeId, "@act", action, "@p", patientId, "@t", targetEmployeeId, "@d", detail);
            }
            catch (Exception)
            {
                // the audit trail must never break the feature that is being audited
            }
        }

        /// <summary>records a refused attempt to reach a patient - at most once a minute per doctor/patient/address</summary>
        public void LogDenied(int actorEmployeeId, int patientId, string path)
        {
            string key = actorEmployeeId + "|" + patientId + "|" + path;
            DateTime last;
            if (_deniedLogged.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
            if (_deniedLogged.Count > 5000) _deniedLogged.Clear();
            _deniedLogged[key] = DateTime.UtcNow;
            Log(actorEmployeeId, "Denied", patientId > 0 ? (int?)patientId : null, null, path);
        }

        public string PatientLabel(int patientId)
        {
            var r = Rows("SELECT PatientCode, FirstName, MiddleName, LastName FROM dbo.PAT_Patient WHERE PatientId = @p", "@p", patientId).FirstOrDefault();
            if (r == null) return "patient #" + patientId;
            return NameOf(r) + " (" + Convert.ToString(r["PatientCode"]) + ")";
        }

        internal static string NameOf(Dictionary<string, object> r)
        {
            var sb = new StringBuilder();
            foreach (string k in new[] { "FirstName", "MiddleName", "LastName" })
            {
                object v;
                if (r.TryGetValue(k, out v) && v != null && Convert.ToString(v).Trim().Length > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(Convert.ToString(v).Trim());
                }
            }
            return sb.ToString();
        }
    }
}
