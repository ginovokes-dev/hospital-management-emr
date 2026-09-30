using System;
using System.Collections.Generic;
using System.Linq;
using DanpheEMR.CommonTypes;
using DanpheEMR.Enums;
using DanpheEMR.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DanpheEMR.CareTeam
{
    public class PatientRef { public int PatientId { get; set; } public string Note { get; set; } public bool NewConsultation { get; set; } }
    public class ShareRequest { public int PatientId { get; set; } public int ToEmployeeId { get; set; } public string Message { get; set; } }
    public class SendRequest { public List<int> ToEmployeeIds { get; set; } public string Subject { get; set; } public string Body { get; set; } public int? PatientId { get; set; } }
    public class MessageRef { public int MessageId { get; set; } }
    public class PasswordRequest { public int UserId { get; set; } public string NewPassword { get; set; } public bool MustChange { get; set; } }
    public class WithdrawRequest { public int UserId { get; set; } public int? HandOverToEmployeeId { get; set; } public string Reason { get; set; } }
    public class UserRef { public int UserId { get; set; } }

    /// <summary>shared plumbing: who is calling, and the application's usual { Status, Results, ErrorMessage } answer</summary>
    public abstract class CareTeamControllerBase : Controller
    {
        protected readonly CareTeamStore Store;
        private readonly ILogger _log;

        protected CareTeamControllerBase(CareTeamStore store, ILoggerFactory loggerFactory)
        {
            Store = store;
            _log = loggerFactory.CreateLogger(GetType().Name);
        }

        protected CallerInfo Me { get { return DanpheAccessFilter.CallerOf(HttpContext); } }

        protected IActionResult Run(Func<object> work)
        {
            var response = new DanpheHTTPResponse<object>();
            try
            {
                if (Me == null) throw new UserFacingException(DanpheAccessFilter.MsgNotSignedIn);
                response.Results = work();
                response.Status = ENUM_Danphe_HTTP_ResponseStatus.OK;
            }
            catch (UserFacingException ex)
            {
                response.Status = ENUM_Danphe_HTTP_ResponseStatus.Failed;
                response.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _log.LogError(0, ex, "Request failed: " + ex.Message);
                response.Status = ENUM_Danphe_HTTP_ResponseStatus.Failed;
                response.ErrorMessage = "Something went wrong. Please try again, and tell the administrator if it keeps happening.";
            }
            return Ok(response);
        }

        protected CallerInfo Doctor()
        {
            var me = Me;
            if (me.EmployeeId <= 0) throw new UserFacingException("Your login is not linked to a staff record. Please ask the administrator.");
            return me;
        }

        protected CallerInfo Admin()
        {
            var me = Me;
            if (!me.IsSuperAdmin) throw new UserFacingException(DanpheAccessFilter.MsgAdminOnly);
            return me;
        }
    }

    /// <summary>Care Team: my patients, find a patient, add myself, share, leave, register, open the chart</summary>
    [Route("api/[controller]")]
    public class CareTeamController : CareTeamControllerBase
    {
        public CareTeamController(CareTeamStore store, ILoggerFactory lf) : base(store, lf) { }

        [HttpGet, Route("Summary")]
        public IActionResult Summary() { return Run(() => Store.Summary(Doctor().EmployeeId)); }

        [HttpGet, Route("MyPatients")]
        public IActionResult MyPatients(string search, int take = 200) { return Run(() => Store.MyPatients(Doctor().EmployeeId, search, take)); }

        [HttpGet, Route("FindPatient")]
        public IActionResult FindPatient(string search)
        {
            return Run(() =>
            {
                if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2) throw new UserFacingException("Type at least two letters of the patient's name, or the patient number.");
                return Store.FindPatients(Doctor().EmployeeId, search, 25);
            });
        }

        [HttpGet, Route("Directory")]
        public IActionResult Directory(bool doctorsOnly = true) { return Run(() => Store.Directory(Doctor().EmployeeId, doctorsOnly)); }

        [HttpGet, Route("Team")]
        public IActionResult Team(int patientId)
        {
            return Run(() =>
            {
                var me = Doctor();
                if (!Store.IsInCareTeam(me.EmployeeId, patientId)) throw new UserFacingException(DanpheAccessFilter.MsgNotYourPatient);
                return Store.TeamOf(patientId);
            });
        }

        [HttpPost, Route("AddToMyCare")]
        public IActionResult AddToMyCare([FromBody] PatientRef r)
        {
            return Run(() =>
            {
                bool added = Store.AddSelf(Doctor(), r.PatientId, r.Note);
                return new Dictionary<string, object> { { "Added", added }, { "Message", added ? "The patient is now on your list." : "The patient was already on your list." } };
            });
        }

        [HttpPost, Route("Share")]
        public IActionResult Share([FromBody] ShareRequest r)
        {
            return Run(() =>
            {
                bool added = Store.Share(Doctor(), r.PatientId, r.ToEmployeeId, r.Message);
                return new Dictionary<string, object> { { "Added", added }, { "Message", added ? "The patient has been shared and the doctor has been notified." : "That doctor already had the patient; they have been notified again." } };
            });
        }

        [HttpPost, Route("RemoveMe")]
        public IActionResult RemoveMe([FromBody] PatientRef r)
        {
            return Run(() => { Store.RemoveSelf(Doctor(), r.PatientId); return "The patient is no longer on your list."; });
        }

        [HttpPost, Route("RegisterPatient")]
        public IActionResult RegisterPatient([FromBody] RegisterPatientInput r) { return Run(() => Store.RegisterPatient(Doctor(), r)); }

        /// <summary>what the patient-record screens need to open a patient: the patient and the visit they work on</summary>
        [HttpPost, Route("OpenChart")]
        public IActionResult OpenChart([FromBody] PatientRef r)
        {
            return Run(() =>
            {
                var me = Doctor();
                var visit = Store.VisitForChart(me, r.PatientId, r.NewConsultation);
                var patient = Store.PatientBasics(r.PatientId);
                var emp = Store.Rows("SELECT FullName FROM dbo.EMP_Employee WHERE EmployeeId = @e", "@e", me.EmployeeId).FirstOrDefault();
                return new Dictionary<string, object> { { "Patient", patient }, { "Visit", visit }, { "PerformerName", emp == null ? me.DisplayName : emp["FullName"] } };
            });
        }
    }

    /// <summary>in-app messages between staff</summary>
    [Route("api/[controller]")]
    public class MessagesController : CareTeamControllerBase
    {
        public MessagesController(CareTeamStore store, ILoggerFactory lf) : base(store, lf) { }

        [HttpGet, Route("UnreadCount")]
        public IActionResult UnreadCount() { return Run(() => Store.UnreadCount(Me.EmployeeId)); }

        [HttpGet, Route("Inbox")]
        public IActionResult Inbox(int take = 50, int skip = 0) { return Run(() => Store.Inbox(Me.EmployeeId, take, skip)); }

        [HttpGet, Route("Sent")]
        public IActionResult Sent(int take = 50, int skip = 0) { return Run(() => Store.Sent(Me.EmployeeId, take, skip)); }

        [HttpGet, Route("Message")]
        public IActionResult Message(int messageId) { return Run(() => Store.GetMessage(Me, messageId)); }

        [HttpPost, Route("Send")]
        public IActionResult Send([FromBody] SendRequest r)
        {
            return Run(() =>
            {
                int n = Store.Send(Me, r.ToEmployeeIds, r.Subject, r.Body, r.PatientId);
                return n == 1 ? "Message sent." : "Message sent to " + n + " people.";
            });
        }

        [HttpPost, Route("Delete")]
        public IActionResult Delete([FromBody] MessageRef r) { return Run(() => { Store.DeleteMessage(Me, r.MessageId); return "Deleted."; }); }

        [HttpPost, Route("MarkAllRead")]
        public IActionResult MarkAllRead() { return Run(() => { Store.MarkAllRead(Me.EmployeeId); return "All messages are marked as read."; }); }

        /// <summary>everybody with a login who can be written to</summary>
        [HttpGet, Route("People")]
        public IActionResult People() { return Run(() => Store.Directory(Me.EmployeeId, false)); }
    }

    /// <summary>Manage Doctors - administrator only (also enforced for every request by DanpheAccessFilter)</summary>
    [Route("api/[controller]")]
    public class DoctorAdminController : CareTeamControllerBase
    {
        public DoctorAdminController(CareTeamStore store, ILoggerFactory lf) : base(store, lf) { }

        [HttpGet, Route("Lookups")]
        public IActionResult Lookups() { return Run(() => { Admin(); return Store.AdminLookups(); }); }

        [HttpGet, Route("Staff")]
        public IActionResult Staff() { return Run(() => { Admin(); return Store.StaffList(); }); }

        [HttpPost, Route("Staff")]
        public IActionResult CreateStaff([FromBody] StaffInput r) { return Run(() => Store.CreateStaff(Admin(), r)); }

        [HttpPut, Route("Staff")]
        public IActionResult UpdateStaff([FromBody] StaffInput r) { return Run(() => { Store.UpdateStaff(Admin(), r); return "Saved."; }); }

        [HttpPost, Route("ResetPassword")]
        public IActionResult ResetPassword([FromBody] PasswordRequest r) { return Run(() => { Store.ResetPassword(Admin(), r.UserId, r.NewPassword, r.MustChange); return "The password has been changed."; }); }

        [HttpPost, Route("Withdraw")]
        public IActionResult Withdraw([FromBody] WithdrawRequest r) { return Run(() => { Store.Withdraw(Admin(), r.UserId, r.HandOverToEmployeeId, r.Reason); return "The login has been withdrawn."; }); }

        [HttpPost, Route("Reactivate")]
        public IActionResult Reactivate([FromBody] UserRef r) { return Run(() => { Store.Reactivate(Admin(), r.UserId); return "The login is active again."; }); }

        [HttpGet, Route("AccessLog")]
        public IActionResult AccessLog(int take = 200) { return Run(() => { Admin(); return Store.AccessLog(take); }); }
    }
}
