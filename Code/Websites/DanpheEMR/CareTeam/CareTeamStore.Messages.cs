using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace DanpheEMR.CareTeam
{
    /// <summary>in-app messages between staff ("email" that never leaves the system), optionally about one patient</summary>
    public partial class CareTeamStore
    {
        internal void SendIn(SqlConnection cn, SqlTransaction tx, int fromEmployeeId, IEnumerable<int> toEmployeeIds, string subject, string body, int? patientId, string type)
        {
            foreach (int to in toEmployeeIds.Distinct())
            {
                Execute(cn, tx, @"INSERT dbo.MSG_DoctorMessage (FromEmployeeId, ToEmployeeId, Subject, Body, PatientId, MessageType)
                                  VALUES (@f, @t, @s, @b, @p, @ty)",
                        "@f", fromEmployeeId, "@t", to, "@s", Clip(subject, 200), "@b", Clip(body, 4000) ?? "", "@p", patientId, "@ty", type);
            }
        }

        public int UnreadCount(int employeeId)
        {
            return ToInt(Scalar("SELECT COUNT(*) FROM dbo.MSG_DoctorMessage WHERE ToEmployeeId = @e AND IsRead = 0 AND DeletedByRecipient = 0", "@e", employeeId));
        }

        /// <summary>a person writes to one or more colleagues; a patient may be attached (only one the sender has access to)</summary>
        public int Send(CallerInfo me, IEnumerable<int> toEmployeeIds, string subject, string body, int? patientId)
        {
            var to = (toEmployeeIds ?? new int[0]).Where(i => i > 0 && i != me.EmployeeId).Distinct().ToList();
            if (to.Count == 0) throw new UserFacingException("Please choose who the message is for.");
            if (string.IsNullOrWhiteSpace(body)) throw new UserFacingException("Please write a message.");
            if (body.Length > 4000) throw new UserFacingException("The message is too long (4000 characters at most).");

            var valid = Directory(me.EmployeeId, false).Select(d => ToInt(d["EmployeeId"])).ToList();
            if (to.Any(t => !valid.Contains(t))) throw new UserFacingException("One of the people you chose could not be found (or has been withdrawn).");

            if (patientId.HasValue && patientId.Value > 0)
            {
                if (!me.IsSuperAdmin && !IsInCareTeam(me.EmployeeId, patientId.Value))
                    throw new UserFacingException("You can only attach patients that are under your care.");
                EnsureActivePatient(patientId.Value);
            }
            else patientId = null;

            using (var cn = Open())
            using (var tx = cn.BeginTransaction())
            {
                SendIn(cn, tx, me.EmployeeId, to, string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject, body, patientId, "Message");
                tx.Commit();
            }
            if (patientId.HasValue) Log(me.EmployeeId, "MessagePatient", patientId, to.Count == 1 ? (int?)to[0] : null, Clip(subject, 200));
            return to.Count;
        }

        public List<Dictionary<string, object>> Inbox(int employeeId, int take, int skip)
        {
            return MessageList(employeeId, true, take, skip);
        }

        public List<Dictionary<string, object>> Sent(int employeeId, int take, int skip)
        {
            return MessageList(employeeId, false, take, skip);
        }

        private List<Dictionary<string, object>> MessageList(int employeeId, bool inbox, int take, int skip)
        {
            take = Math.Min(Math.Max(take, 1), 200);
            skip = Math.Max(skip, 0);
            string sql = @"SELECT m.MessageId, m.Subject, LEFT(m.Body, 160) AS Preview, m.MessageType, m.SentOn, m.IsRead, m.PatientId,
                                  m.FromEmployeeId, fe.FullName AS FromName, m.ToEmployeeId, te.FullName AS ToName,
                                  p.PatientCode, p.FirstName, p.MiddleName, p.LastName
                             FROM dbo.MSG_DoctorMessage m
                             JOIN dbo.EMP_Employee fe ON fe.EmployeeId = m.FromEmployeeId
                             JOIN dbo.EMP_Employee te ON te.EmployeeId = m.ToEmployeeId
                             LEFT JOIN dbo.PAT_Patient p ON p.PatientId = m.PatientId
                            WHERE " + (inbox ? "m.ToEmployeeId = @e AND m.DeletedByRecipient = 0" : "m.FromEmployeeId = @e AND m.DeletedBySender = 0") + @"
                            ORDER BY m.SentOn DESC, m.MessageId DESC
                            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
            var rows = Rows(sql, "@e", employeeId, "@skip", skip, "@take", take);
            foreach (var r in rows) r["PatientName"] = r["PatientId"] == null ? null : NameOf(r);
            return rows;
        }

        /// <summary>the whole message. Opening it as the recipient marks it as read.</summary>
        public Dictionary<string, object> GetMessage(CallerInfo me, int messageId)
        {
            var m = Rows(@"SELECT m.MessageId, m.Subject, m.Body, m.MessageType, m.SentOn, m.IsRead, m.PatientId,
                                  m.FromEmployeeId, fe.FullName AS FromName, fe.Speciality AS FromSpeciality, m.ToEmployeeId, te.FullName AS ToName,
                                  m.DeletedBySender, m.DeletedByRecipient,
                                  p.PatientCode, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.Age
                             FROM dbo.MSG_DoctorMessage m
                             JOIN dbo.EMP_Employee fe ON fe.EmployeeId = m.FromEmployeeId
                             JOIN dbo.EMP_Employee te ON te.EmployeeId = m.ToEmployeeId
                             LEFT JOIN dbo.PAT_Patient p ON p.PatientId = m.PatientId
                            WHERE m.MessageId = @m", "@m", messageId).FirstOrDefault();
            bool mineAsRecipient = m != null && ToInt(m["ToEmployeeId"]) == me.EmployeeId && !ToBool(m["DeletedByRecipient"]);
            bool mineAsSender = m != null && ToInt(m["FromEmployeeId"]) == me.EmployeeId && !ToBool(m["DeletedBySender"]);
            if (m == null || (!mineAsRecipient && !mineAsSender)) throw new UserFacingException("That message could not be found.");

            if (mineAsRecipient && !ToBool(m["IsRead"]))
            {
                Execute("UPDATE dbo.MSG_DoctorMessage SET IsRead = 1, ReadOn = GETDATE() WHERE MessageId = @m", "@m", messageId);
                m["IsRead"] = true;
            }
            if (m["PatientId"] != null)
            {
                m["PatientName"] = NameOf(m);
                m["PatientOnMyTeam"] = IsInCareTeam(me.EmployeeId, ToInt(m["PatientId"]));
            }
            return m;
        }

        public void DeleteMessage(CallerInfo me, int messageId)
        {
            int a = Execute("UPDATE dbo.MSG_DoctorMessage SET DeletedByRecipient = 1, IsRead = 1 WHERE MessageId = @m AND ToEmployeeId = @e", "@m", messageId, "@e", me.EmployeeId);
            int b = Execute("UPDATE dbo.MSG_DoctorMessage SET DeletedBySender = 1 WHERE MessageId = @m AND FromEmployeeId = @e", "@m", messageId, "@e", me.EmployeeId);
            if (a + b == 0) throw new UserFacingException("That message could not be found.");
        }

        public void MarkAllRead(int employeeId)
        {
            Execute("UPDATE dbo.MSG_DoctorMessage SET IsRead = 1, ReadOn = GETDATE() WHERE ToEmployeeId = @e AND IsRead = 0", "@e", employeeId);
        }
    }
}
