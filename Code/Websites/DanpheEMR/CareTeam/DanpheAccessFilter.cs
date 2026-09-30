using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Text;
using DanpheEMR.CommonTypes;
using DanpheEMR.Core.Configuration;
using DanpheEMR.Enums;
using DanpheEMR.Security;
using DanpheEMR.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DanpheEMR.CareTeam
{
    /// <summary>
    /// The one place that decides who may call what on the server. Registered for EVERY controller action (Startup.cs), so nothing
    /// depends on whether an individual controller remembered to check.
    ///
    ///   1. Who are you?   The token in the Authorization header must be signed by this server and not expired, the sign-in
    ///                     session must exist and belong to the same person, and the login (and the employee) must still be switched on.
    ///   2. Administrator-only calls (logins, roles, permissions, the Manage Doctors screen) need the administrator role.
    ///   3. Doctors ("confined" roles) may only call what the clinical screens need, may only name patients on their own care
    ///      team, and everything they get back is filtered down to those patients.
    ///
    /// Refusals use the application's usual answer: HTTP 200 with Status "Failed" and a readable ErrorMessage.
    /// </summary>
    public class DanpheAccessFilter : IAuthorizationFilter, IResultFilter, IOrderedFilter
    {
        public const string CallerItemKey = "danphe.caller";
        private const int MaxBodyToInspect = 8 * 1024 * 1024;

        public const string MsgNotSignedIn = "Unauthorized Access";
        public const string MsgAdminOnly = "Only the administrator can do this.";
        public const string MsgNotForDoctors = "This part of the system is not available to doctor accounts.";
        public const string MsgOwnRecordOnly = "You can only open or change your own profile.";
        public const string MsgBadUpload = "That file could not be accepted (its name must not contain folders).";
        public const string MsgNotYourPatient = "This patient is not under your care. Open Care Team > Find Patient, look them up and choose \"Add to my care\" to continue.";

        private readonly CareTeamStore _store;
        private readonly TokenValidationParameters _tokenRules;

        public DanpheAccessFilter(CareTeamStore store, IOptions<MyConfiguration> config)
        {
            _store = store;
            string key = config.Value.JwtTokenConfig != null ? config.Value.JwtTokenConfig.JwtKey : null;
            _tokenRules = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? Guid.NewGuid().ToString("N"))),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        }

        /// <summary>runs before the application's own filters</summary>
        public int Order { get { return -1000; } }

        public static CallerInfo CallerOf(HttpContext http)
        {
            object o;
            return http.Items.TryGetValue(CallerItemKey, out o) ? o as CallerInfo : null;
        }

        private static JsonResult Refuse(string message)
        {
            return new JsonResult(new DanpheHTTPResponse<object> { Status = ENUM_Danphe_HTTP_ResponseStatus.Failed, ErrorMessage = message, Results = "" });
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // before the action runs
        // ---------------------------------------------------------------------------------------------------------------------
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
            if (descriptor == null) return;
            if (AccessRules.IsPublic(descriptor.ControllerName)) return;

            var http = context.HttpContext;
            var req = http.Request;
            string path = req.Path.Value ?? "";
            // the DICOM listener program posts here and is checked by the existing filter
            if (string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase) && string.Equals(path, "/api/Dicom", StringComparison.OrdinalIgnoreCase)) return;
            if (context.Filters.Any(f => f is IAllowAnonymousFilter)) return;

            CallerInfo caller = Identify(http, path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase));
            if (caller == null || !caller.IsActive)
            {
                context.Result = Refuse(MsgNotSignedIn);
                return;
            }
            http.Items[CallerItemKey] = caller;

            string controller = descriptor.ControllerName;
            if (AccessRules.RequiresAdmin(controller, req.Method) && !caller.IsSuperAdmin)
            {
                context.Result = Refuse(MsgAdminOnly);
                return;
            }
            string routeKey = AccessRules.RouteKey(descriptor);
            // nobody but the administrator changes another person's landing page
            if (!caller.IsSuperAdmin && routeKey == "employee/landingpage" && !IsOwnRecord(context, caller, routeKey))
            {
                context.Result = Refuse(MsgOwnRecordOnly);
                return;
            }
            if (!caller.Confined) return;

            // ---- a doctor ----
            if (caller.EmployeeId <= 0 || !AccessRules.DoctorMayUse(controller, routeKey, req.Method))
            {
                context.Result = Refuse(MsgNotForDoctors);
                return;
            }
            if (AccessRules.IsOwnRecordEndpoint(routeKey) && !IsOwnRecord(context, caller, routeKey))
            {
                context.Result = Refuse(MsgOwnRecordOnly);
                return;
            }
            // an uploaded file becomes part of a path on the server: doctors may only send plain file names
            if (IsMultipart(req))
            {
                bool badName;
                if (!TryCheckUploadNames(req, out badName) || badName)
                {
                    context.Result = Refuse(MsgBadUpload);
                    return;
                }
            }
            if (!AccessRules.ChecksPatientIds(controller, routeKey)) return;

            var ids = new NamedIds(routeKey);
            var patientIds = ids.Patients;
            var visitIds = ids.Visits;
            foreach (var q in req.Query) ids.Add(q.Key, q.Value);
            foreach (var rv in context.RouteData.Values) ids.Add(rv.Key, new[] { Convert.ToString(rv.Value) });
            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMultipart(req))
                {
                    // an upload of a scan / document: the patient is named in one of the form's fields, usually as JSON
                    IFormCollection form;
                    try { form = req.Form; }
                    catch (Exception)
                    {
                        context.Result = Refuse("That request is too large.");
                        return;
                    }
                    foreach (var field in form)
                    {
                        ids.Add(field.Key, field.Value);
                        foreach (string value in field.Value)
                        {
                            JToken parsed = TryParse(value);
                            if (parsed != null) ids.CollectFrom(parsed, 0);
                        }
                    }
                }
                else
                {
                    bool tooLarge;
                    string body = ReadBody(req, out tooLarge);
                    if (tooLarge)
                    {
                        context.Result = Refuse("That request is too large.");
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        // the application often posts JSON with a "form" content type, so the body is always read as text
                        JToken root = TryParse(body);
                        var asText = root as JValue;
                        if (asText != null && asText.Type == JTokenType.String) root = TryParse(Convert.ToString(asText.Value));   // JSON inside a JSON string
                        if (root != null) ids.CollectFrom(root, 0);
                        else if (req.HasFormContentType)
                        {
                            // a genuine key=value&key=value body
                            try { foreach (var pair in QueryHelpers.ParseQuery(body.Trim())) ids.Add(pair.Key, pair.Value); }
                            catch (Exception) { }
                        }
                    }
                }
            }

            // record numbers (a note, an allergy, an uploaded file ...) belong to a patient as well
            foreach (var entity in ids.Records)
                foreach (var owner in _store.GetPatientIdsForRecords(entity.Key, entity.Value))
                    if (owner.Value > 0) patientIds.Add(owner.Value);

            int blocked = 0;
            foreach (int pid in patientIds)
                if (pid > 0 && !_store.IsInCareTeam(caller.EmployeeId, pid)) { blocked = pid; break; }
            if (blocked == 0 && visitIds.Count > 0)
            {
                var map = _store.GetPatientIdsForVisits(visitIds);
                foreach (int vid in visitIds)
                {
                    int pid;
                    if (map.TryGetValue(vid, out pid) && pid > 0 && !_store.IsInCareTeam(caller.EmployeeId, pid)) { blocked = pid; break; }
                }
            }
            if (blocked > 0)
            {
                _store.LogDenied(caller.EmployeeId, blocked, req.Method + " " + path);
                context.Result = Refuse(MsgNotYourPatient);
            }
        }

        /// <summary>the profile screens may only read / change the signed-in doctor's own record</summary>
        private static bool IsOwnRecord(AuthorizationFilterContext context, CallerInfo caller, string routeKey)
        {
            var req = context.HttpContext.Request;
            if (routeKey == "employee/profile")
            {
                int emp;
                return int.TryParse(req.Query["empId"], out emp) && emp == caller.EmployeeId;
            }
            // employee/landingpage: the body names the login whose landing page is set
            bool tooLarge;
            JToken body = TryParse(ReadBody(req, out tooLarge));
            var obj = body as JObject;
            if (tooLarge || obj == null) return false;
            int userId;
            return int.TryParse(Convert.ToString(obj["UserId"]), out userId) && userId == caller.UserId;
        }

        /// <summary>the person behind this request, or null when they are not (or no longer) allowed to be here</summary>
        private CallerInfo Identify(HttpContext http, bool isApi)
        {
            int? tokenUser = null;
            string header = http.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(header))
            {
                tokenUser = UserFromToken(header);
                if (tokenUser == null) return null;          // a token that does not check out is never ignored
            }
            else if (isApi)
            {
                return null;                                 // API calls always carry the token (as before)
            }

            RbacUser sessionUser = http.Session.Get<RbacUser>(ENUM_SessionVariables.CurrentUser);
            if (sessionUser == null) return null;            // signed out / session ended / server restarted
            if (tokenUser.HasValue && tokenUser.Value != sessionUser.UserId) return null;

            return _store.GetCaller(sessionUser.UserId);
        }

        private int? UserFromToken(string authorizationHeader)
        {
            try
            {
                string[] parts = authorizationHeader.Split(' ');
                if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase)) return null;
                SecurityToken validated;
                var principal = new JwtSecurityTokenHandler().ValidateToken(parts[1], _tokenRules, out validated);
                var jwt = validated as JwtSecurityToken;
                if (jwt == null || !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal)) return null;
                string claim = principal.Claims.Where(c => c.Type == ENUM_ClaimTypes.currentUser).Select(c => c.Value).FirstOrDefault();
                if (string.IsNullOrEmpty(claim)) return null;
                int id;
                return int.TryParse(Convert.ToString(JObject.Parse(claim)["UserId"]), out id) && id > 0 ? (int?)id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // reading ids out of a request
        // ---------------------------------------------------------------------------------------------------------------------
        private static string ReadBody(HttpRequest req, out bool tooLarge)
        {
            tooLarge = false;
            Stream body = req.Body;
            if (body == null || !body.CanSeek) return null;
            if (req.ContentType != null && req.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) return null;   // uploads: the query string is checked
            if (req.ContentLength.HasValue && req.ContentLength.Value > MaxBodyToInspect) { tooLarge = true; return null; }
            body.Seek(0, SeekOrigin.Begin);
            string text = new StreamReader(body, Encoding.UTF8, true, 4096, true).ReadToEnd();
            body.Seek(0, SeekOrigin.Begin);
            if (text.Length > MaxBodyToInspect) { tooLarge = true; return null; }
            return text;
        }

        private static JToken TryParse(string text)
        {
            text = text == null ? null : text.Trim();
            if (string.IsNullOrEmpty(text) || (text[0] != '{' && text[0] != '[' && text[0] != '"')) return null;
            try { return JToken.Parse(text); }
            catch (Exception) { return null; }
        }

        private static void AddInt(string s, HashSet<int> into)
        {
            int n;
            if (int.TryParse(s, out n) && n > 0) into.Add(n);
        }

        private static void AddInts(IEnumerable<string> values, HashSet<int> into)
        {
            foreach (string v in values)
                foreach (string piece in (v ?? "").Split(','))
                    AddInt(piece.Trim(), into);
        }

        /// <summary>the patient / visit / record numbers a request names, wherever it puts them</summary>
        private class NamedIds
        {
            private readonly string _route;
            public readonly HashSet<int> Patients = new HashSet<int>();
            public readonly HashSet<int> Visits = new HashSet<int>();
            /// <summary>"Table.Column" -> record numbers</summary>
            public readonly Dictionary<string, HashSet<int>> Records = new Dictionary<string, HashSet<int>>();

            public NamedIds(string routeKey)
            {
                _route = routeKey;
            }

            public void Add(string name, IEnumerable<string> values)
            {
                HashSet<int> into = null;
                if (AccessRules.IsPatientKey(name)) into = Patients;
                else if (AccessRules.IsVisitKey(name)) into = Visits;
                else
                {
                    string column = AccessRules.EntityColumn(name, _route);
                    if (column != null)
                    {
                        if (!Records.TryGetValue(column, out into)) Records[column] = into = new HashSet<int>();
                    }
                }
                if (into != null) AddInts(values, into);
            }

            public void CollectFrom(JToken t, int depth)
            {
                if (depth > 40) return;
                var obj = t as JObject;
                if (obj != null)
                {
                    foreach (var p in obj.Properties())
                    {
                        var value = p.Value as JValue;
                        if (value != null) Add(p.Name, new[] { Convert.ToString(value.Value) });
                        else if (p.Value is JObject || p.Value is JArray) CollectFrom(p.Value, depth + 1);
                    }
                    return;
                }
                var arr = t as JArray;
                if (arr != null)
                    foreach (var el in arr)
                        if (el is JObject || el is JArray) CollectFrom(el, depth + 1);
            }
        }

        private static bool IsMultipart(HttpRequest req)
        {
            return req.ContentType != null && req.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>false when the upload cannot be read; badName is true when a file carries a path in its name</summary>
        private static bool TryCheckUploadNames(HttpRequest req, out bool badName)
        {
            badName = false;
            try
            {
                foreach (var file in req.Form.Files)
                    if (!AccessRules.IsPlainFileName(file.FileName)) badName = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // after the action ran: a doctor only gets to see rows of their own patients
        // ---------------------------------------------------------------------------------------------------------------------
        public void OnResultExecuting(ResultExecutingContext context)
        {
            var caller = CallerOf(context.HttpContext);
            if (caller == null || !caller.Confined) return;
            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
            if (descriptor == null || !AccessRules.ScrubsResults(descriptor.ControllerName, AccessRules.RouteKey(descriptor))) return;

            try
            {
                ScrubResult(context, caller);
            }
            catch (Exception)
            {
                // if the answer cannot be checked it is not handed out
                context.Result = Refuse(MsgNotYourPatient);
            }
        }

        public void OnResultExecuted(ResultExecutedContext context)
        {
        }

        private class ScrubContext
        {
            public HashSet<int> Allowed;
            public Dictionary<int, int> VisitToPatient = new Dictionary<int, int>();
        }

        private void ScrubResult(ResultExecutingContext context, CallerInfo caller)
        {
            var objectResult = context.Result as ObjectResult;
            var jsonResult = context.Result as JsonResult;
            var contentResult = context.Result as ContentResult;

            object value = objectResult != null ? objectResult.Value : (jsonResult != null ? jsonResult.Value : null);
            JToken token = null;
            bool wasText = false;

            if (contentResult != null)
            {
                if (contentResult.Content == null || !LooksLikeJson(contentResult.Content)) return;
                token = TryParse(contentResult.Content);
                wasText = true;
            }
            else if (value == null) return;
            else if (value is JToken) token = (JToken)value;
            else if (value is string)
            {
                string s = (string)value;
                if (!LooksLikeJson(s)) return;
                token = TryParse(s);
                wasText = true;
            }
            else
            {
                var serializer = new JsonSerializer { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                token = JToken.FromObject(value, serializer);
            }
            if (token == null) return;

            // find which patients / visits the answer talks about
            var patients = new HashSet<int>();
            var visits = new HashSet<int>();
            CollectAnswerIds(token, patients, visits, 0);
            if (patients.Count == 0 && visits.Count == 0) return;

            var sc = new ScrubContext { Allowed = _store.GetCareTeamPatientIds(caller.EmployeeId, true) };
            if (visits.Count > 0) sc.VisitToPatient = _store.GetPatientIdsForVisits(visits);

            bool refusedWhole;
            var root = token as JObject;
            if (root != null && root["Results"] != null && root["Status"] != null)
            {
                refusedWhole = Clean(root["Results"], sc);
                if (refusedWhole)
                {
                    root["Status"] = ENUM_Danphe_HTTP_ResponseStatus.Failed;
                    root["ErrorMessage"] = MsgNotYourPatient;
                    root["Results"] = "";
                }
            }
            else
            {
                refusedWhole = Clean(token, sc);
                if (refusedWhole)
                {
                    context.Result = Refuse(MsgNotYourPatient);
                    return;
                }
            }

            if (contentResult != null) contentResult.Content = token.ToString(Formatting.None);
            else if (objectResult != null) objectResult.Value = wasText ? (object)token.ToString(Formatting.None) : token;
            else if (jsonResult != null) jsonResult.Value = token;
        }

        private static bool LooksLikeJson(string s)
        {
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == '﻿') continue;
                return c == '{' || c == '[';
            }
            return false;
        }

        private static int IntOf(JToken v)
        {
            var jv = v as JValue;
            if (jv == null || jv.Value == null) return 0;
            int n;
            return int.TryParse(Convert.ToString(jv.Value), out n) ? n : 0;
        }

        /// <summary>every PatientId / visit number that appears anywhere in the answer</summary>
        private static void CollectAnswerIds(JToken t, HashSet<int> patients, HashSet<int> visits, int depth)
        {
            if (depth > 60) return;
            var obj = t as JObject;
            if (obj != null)
            {
                bool hasPatient = false;
                int visitId = 0;
                foreach (var p in obj.Properties())
                {
                    if (AccessRules.IsPatientKey(p.Name)) { int n = IntOf(p.Value); if (n > 0) { patients.Add(n); hasPatient = true; } }
                    else if (visitId == 0 && AccessRules.IsVisitKey(p.Name)) visitId = IntOf(p.Value);
                    else if (p.Value is JObject || p.Value is JArray) CollectAnswerIds(p.Value, patients, visits, depth + 1);
                }
                if (!hasPatient && visitId > 0) visits.Add(visitId);
                return;
            }
            var arr = t as JArray;
            if (arr != null)
                foreach (var el in arr)
                    if (el is JObject || el is JArray) CollectAnswerIds(el, patients, visits, depth + 1);
        }

        /// <summary>
        /// Removes what belongs to other doctors' patients. Rows of a list are dropped one by one; a single record that is foreign
        /// makes the whole answer a refusal (returns true).
        /// </summary>
        private static bool Clean(JToken t, ScrubContext sc)
        {
            var arr = t as JArray;
            if (arr != null)
            {
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    JToken el = arr[i];
                    if ((el is JObject || el is JArray) && Clean(el, sc)) arr.RemoveAt(i);
                }
                return false;
            }
            var obj = t as JObject;
            if (obj == null) return false;

            bool hasPatient = false;
            int visitId = 0;
            foreach (var p in obj.Properties())
            {
                if (AccessRules.IsPatientKey(p.Name))
                {
                    int n = IntOf(p.Value);
                    if (n > 0) { hasPatient = true; if (!sc.Allowed.Contains(n)) return true; }
                }
                else if (visitId == 0 && AccessRules.IsVisitKey(p.Name)) visitId = IntOf(p.Value);
            }
            if (!hasPatient && visitId > 0)
            {
                int owner;
                if (sc.VisitToPatient.TryGetValue(visitId, out owner) && owner > 0 && !sc.Allowed.Contains(owner)) return true;
            }
            foreach (var p in obj.Properties().ToList())
            {
                if (p.Value is JArray) Clean(p.Value, sc);
                else if (p.Value is JObject && Clean(p.Value, sc)) return true;
            }
            return false;
        }
    }
}
