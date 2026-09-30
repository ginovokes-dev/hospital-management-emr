using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace DanpheEMR.CareTeam
{
    /// <summary>
    /// The written-down rules behind DanpheAccessFilter. Everything is keyed on the controller's name (or on the route of one action),
    /// never on the address the browser typed, so different spellings of the same address cannot get around a rule.
    ///
    /// Three kinds of people:
    ///   * the administrator (a role flagged IsSysAdmin)      - everything; the only one who may manage logins
    ///   * doctors ("confined": a role flagged ConfineToCareTeam) - the clinical screens plus the lookups those screens need, and
    ///                                                              only for patients on their own care team
    ///   * everybody else (billing, pharmacy ... of the original product) - unchanged, except that a login is now required everywhere
    /// </summary>
    internal static class AccessRules
    {
        private static bool Is(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool In(string name, string[] list)
        {
            return list.Any(x => Is(x, name));
        }

        /// <summary>the sign-in pages and the page shell: no check here (they have their own)</summary>
        public static bool IsPublic(string controller)
        {
            return Is(controller, "Account") || Is(controller, "Home") || controller.EndsWith("View", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>only the administrator: creating / changing logins, roles and permissions</summary>
        private static readonly string[] AdminOnly = { "SecuritySettings", "DoctorAdmin" };

        /// <summary>only the administrator may change these (everybody may still read them: the lists of staff the screens use)</summary>
        private static readonly string[] AdminOnlyForWrites = { "EmployeeSettings" };

        public static bool RequiresAdmin(string controller, string httpMethod)
        {
            if (In(controller, AdminOnly)) return true;
            bool write = !(Is(httpMethod, "GET") || Is(httpMethod, "HEAD") || Is(httpMethod, "OPTIONS"));
            return write && In(controller, AdminOnlyForWrites);
        }

        /// <summary>doctors: whole controllers of the clinical screens</summary>
        private static readonly string[] ClinicalControllers =
        {
            "Clinical", "Doctors", "VisitSummary", "Orders", "ClnPsychiatry", "DynTemplates", "DynamicTemplate", "PharmacyPrescription",
            "Visit", "Admission", "Nursing"
        };

        /// <summary>doctors: the new screens (their own rules inside)</summary>
        private static readonly string[] OwnRulesControllers = { "CareTeam", "Messages", "Notification" };

        /// <summary>
        /// doctors: single lookups the screens call while loading (lower-case "controller/action" from the route; a trailing /* means
        /// everything below it). Read-only unless the entry says otherwise.
        /// </summary>
        private static readonly string[] Lookups =
        {
            "security/*", "parameters", "master/*", "core/*",
            "settings/countrysubdivisions", "settings/printexportconfiguration", "settings/getpaymentmodesettings", "settings/getpaymentmodes",
            "employeesettings/*",
            "billing/billingcounters", "billing/paymentpages", "billsettings/printersettings", "billsettings/membershiptypes",
            "billingmaster/schemepricecategoriesmap", "billingmaster/schemes",
            "lab/labtypes", "lab/labtests", "labsetting/labgovreportingitems",
            "accounting/accountingcodes", "accounting/fiscalyears",
            "pharmacy", "pharmacysettings/generics", "pharmacysettings/items",
            "radiology/imagingitems",
            "reporting/homedashboardstats", "reporting/patientzonemap", "reporting/departmentappointmentstotal"
        };

        /// <summary>lookups a doctor's screen also WRITES to (their own preferences)</summary>
        private static readonly string[] LookupWrites = { "core/employeedatepreference" };

        /// <summary>
        /// doctors: reading about ONE patient (or one of the patient's files) from the Patient controller, which the "Clinical Documents"
        /// tab of a patient record does. They only work for patients on the doctor's own care team (see ChecksPatientIds).
        /// </summary>
        private static readonly string[] PatientScopedReads = { "patient/lightpatientbyid", "patient/patientdocuments", "patient/downloadfile" };

        /// <summary>
        /// doctors: signing a lab / imaging order saves it as a provisional bill of the patient's visit (billing settles it later). The body
        /// names the patient and the visit, so it only works for patients on the doctor's own care team.
        /// </summary>
        private static readonly string[] PatientScopedWrites = { "billing/provisional-billing" };

        public static bool IsPatientScopedCall(string routeKey)
        {
            return PatientScopedReads.Any(p => Matches(routeKey, p)) || PatientScopedWrites.Any(p => Matches(routeKey, p));
        }

        /// <summary>doctor-visible controllers whose answers are filtered down to the doctor's own patients</summary>
        public static bool ScrubsResults(string controller, string routeKey)
        {
            return In(controller, ClinicalControllers) || IsPatientScopedCall(routeKey);
        }

        /// <summary>doctor-visible calls that may only name patients (and visits, and records) of the doctor's own care team</summary>
        public static bool ChecksPatientIds(string controller, string routeKey)
        {
            return In(controller, ClinicalControllers) || IsPatientScopedCall(routeKey);
        }

        public static string RouteKey(ControllerActionDescriptor d)
        {
            string t = d.AttributeRouteInfo != null ? d.AttributeRouteInfo.Template : null;
            if (string.IsNullOrEmpty(t)) t = d.ControllerName + "/" + d.ActionName;
            t = t.Trim('/').ToLowerInvariant();
            if (t.StartsWith("api/")) t = t.Substring(4);
            return t;
        }

        private static bool Matches(string key, string pattern)
        {
            if (pattern.EndsWith("/*")) return key == pattern.Substring(0, pattern.Length - 2) || key.StartsWith(pattern.Substring(0, pattern.Length - 1));
            return key == pattern;
        }

        /// <summary>doctors: the profile / landing-page calls of the "my profile" screens - allowed for their own record only</summary>
        public static bool IsOwnRecordEndpoint(string routeKey)
        {
            return routeKey == "employee/profile" || routeKey == "employee/landingpage";
        }

        public static bool DoctorMayUse(string controller, string routeKey, string httpMethod)
        {
            if (In(controller, ClinicalControllers) || In(controller, OwnRulesControllers)) return true;
            if (IsOwnRecordEndpoint(routeKey)) return true;
            bool read = Is(httpMethod, "GET") || Is(httpMethod, "HEAD") || Is(httpMethod, "OPTIONS");
            if (read && PatientScopedReads.Any(p => Matches(routeKey, p))) return true;
            if (Is(httpMethod, "POST") && PatientScopedWrites.Any(p => Matches(routeKey, p))) return true;
            if (Lookups.Any(p => Matches(routeKey, p)))
            {
                if (read) return true;
                return LookupWrites.Any(p => Matches(routeKey, p));
            }
            return false;
        }

        /// <summary>
        /// Record numbers that identify ONE row of a patient's clinical data (name in a query string / route / JSON body -> "Table.Column").
        /// A doctor may only name rows of patients on their care team, even when the request says otherwise about the patient.
        /// </summary>
        private static readonly Dictionary<string, string> EntityColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "NotesId", "CLN_Notes.NotesId" }, { "NoteId", "CLN_Notes.NotesId" },
            { "EmergencyNoteId", "CLN_Notes_Emergency.EmergencyNoteId" }, { "FreeTextId", "CLN_Notes_FreeText.FreeTextId" },
            { "ObjectiveNotesId", "CLN_Notes_Objective.ObjectiveNotesId" }, { "PrescriptionNoteId", "CLN_Notes_PrescriptionNote.PrescriptionNoteId" },
            { "ProcedureNoteId", "CLN_Notes_Procedure.ProcedureNoteId" }, { "ProgressNoteId", "CLN_Notes_Progress.ProgressNoteId" },
            { "SubjectiveNoteId", "CLN_Notes_Subjective.SubjectiveNoteId" },
            { "PatientAllergyId", "CLN_Allergies.PatientAllergyId" }, { "BloodSugarMonitoringId", "CLN_BloodSugarMonitoring.BloodSugarMonitoringId" },
            { "ConsultationRequestId", "CLN_ConsultationRequest.ConsultationRequestId" }, { "DiagnosisId", "CLN_Diagnosis.DiagnosisId" },
            { "FamilyProblemId", "CLN_FamilyHistory.FamilyProblemId" }, { "HomeMedicationId", "CLN_HomeMedications.HomeMedicationId" },
            { "MedicationPrescriptionId", "CLN_MedicationPrescription.MedicationPrescriptionId" }, { "SocialHistoryId", "CLN_SocialHistory.SocialHistoryId" },
            { "SurgicalHistoryId", "CLN_SurgicalHistory.SurgicalHistoryId" }, { "PatientDietId", "CLN_TXN_PatientDiet.PatientDietId" },
            { "PatImageId", "CLN_PAT_Images.PatImageId" }
        };

        /// <summary>"Table.Column" whose PatientId owns the record number called <paramref name="name"/>, or null when it is not a record number</summary>
        public static string EntityColumn(string name, string routeKey)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // the same number means different tables on different screens
            if (Is(name, "PatientProblemId")) return (routeKey ?? "").Contains("past") ? "CLN_PastMedicals.PatientProblemId" : "CLN_ActiveMedicals.PatientProblemId";
            if (Is(name, "PatientFileId")) return (routeKey ?? "").StartsWith("patient/") ? "PAT_PatientFiles.PatientFileId" : "CLN_EyeScanImages.PatientFileId";
            string column;
            return EntityColumns.TryGetValue(name, out column) ? column : null;
        }

        /// <summary>an uploaded file must carry a plain name, never a folder path (it becomes part of a path on the server)</summary>
        public static bool IsPlainFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0) return false;
            return name != "." && name != "..";
        }

        // names that carry a patient / visit number in a query string, a route or a JSON body
        public static bool IsPatientKey(string name)
        {
            return Is(name, "PatientId");
        }

        public static bool IsVisitKey(string name)
        {
            return Is(name, "PatientVisitId") || Is(name, "VisitId") || Is(name, "InPatientVisitId") || Is(name, "IpVisitId");
        }
    }
}
