using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json.Linq;

namespace DanpheEMR.CareTeam
{
    /// <summary>
    /// Guessing passwords is slowed down: after 8 wrong sign-ins for the same user name from the same computer, that combination is
    /// refused for 5 minutes (even with the right password). 60 wrong sign-ins from one computer in 10 minutes block that computer for
    /// 5 minutes. A successful sign-in clears the count. Counted in memory: a restart of the program clears it.
    /// </summary>
    public class LoginThrottle
    {
        private class Entry { public int Fails; public DateTime FirstFail; public DateTime BlockedUntil; }

        private const int MaxFailsPerUser = 8;
        private const int MaxFailsPerComputer = 60;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>();

        private static string UserKey(string user, string ip) { return "u|" + (user ?? "").Trim().ToLowerInvariant() + "|" + ip; }
        private static string ComputerKey(string ip) { return "c|" + ip; }

        /// <summary>minutes left when this attempt must be refused, else 0</summary>
        public int MinutesBlocked(string user, string ip)
        {
            DateTime now = DateTime.UtcNow;
            DateTime until = DateTime.MinValue;
            Entry e;
            if (_entries.TryGetValue(UserKey(user, ip), out e)) lock (e) { if (e.BlockedUntil > until) until = e.BlockedUntil; }
            if (_entries.TryGetValue(ComputerKey(ip), out e)) lock (e) { if (e.BlockedUntil > until) until = e.BlockedUntil; }
            return until > now ? (int)Math.Ceiling((until - now).TotalMinutes) : 0;
        }

        public void Failed(string user, string ip)
        {
            Count(UserKey(user, ip), MaxFailsPerUser);
            Count(ComputerKey(ip), MaxFailsPerComputer);
        }

        public void Succeeded(string user, string ip)
        {
            Entry removed;
            _entries.TryRemove(UserKey(user, ip), out removed);
        }

        private void Count(string key, int limit)
        {
            DateTime now = DateTime.UtcNow;
            if (_entries.Count > 20000) _entries.Clear();
            Entry e = _entries.GetOrAdd(key, k => new Entry { FirstFail = now });
            lock (e)
            {
                if (now - e.FirstFail > Window) { e.Fails = 0; e.FirstFail = now; }
                e.Fails++;
                if (e.Fails >= limit) { e.BlockedUntil = now + BlockFor; e.Fails = 0; e.FirstFail = now; }
            }
        }
    }

    /// <summary>
    /// applies <see cref="LoginThrottle"/> to every call that checks a password without being signed in: the sign-in page, the token call
    /// used by tools and the change-password call (it verifies the current password of any user name it is given, so it would otherwise
    /// be a way around the lockout)
    /// </summary>
    public class LoginThrottleFilter : IAuthorizationFilter, IResultFilter, IOrderedFilter
    {
        private const string KeyUser = "danphe.login.user";
        private static readonly Regex StatusFailed = new Regex("\"Status\"\\s*:\\s*\"Failed\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex StatusOk = new Regex("\"Status\"\\s*:\\s*\"OK\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly LoginThrottle _throttle;

        public LoginThrottleFilter(LoginThrottle throttle) { _throttle = throttle; }

        public int Order { get { return -1100; } }

        private static bool IsSignIn(ControllerActionDescriptor d, HttpRequest req)
        {
            if (d == null || !string.Equals(d.ControllerName, "Account", StringComparison.OrdinalIgnoreCase)) return false;
            if (!HttpMethods.IsPost(req.Method) && !HttpMethods.IsPut(req.Method)) return false;
            return string.Equals(d.ActionName, "Login", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.ActionName, "LoginToDanpheEMR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.ActionName, "ChangePassword", StringComparison.OrdinalIgnoreCase);
        }

        private static string Ip(HttpContext http)
        {
            var ip = http.Connection.RemoteIpAddress;
            return ip == null ? "unknown" : ip.ToString();
        }

        private static string UserNameOf(HttpRequest req)
        {
            try
            {
                if (req.HasFormContentType) return req.Form["UserName"].FirstOrDefault() ?? req.Form["Username"].FirstOrDefault() ?? "";
                if (req.Body != null && req.Body.CanSeek)
                {
                    req.Body.Seek(0, SeekOrigin.Begin);
                    string text = new StreamReader(req.Body, Encoding.UTF8, true, 1024, true).ReadToEnd();
                    req.Body.Seek(0, SeekOrigin.Begin);
                    var obj = JToken.Parse(text) as JObject;
                    if (obj != null) return Convert.ToString(obj["UserName"]) ?? "";
                }
            }
            catch (Exception) { }
            return "";
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var req = context.HttpContext.Request;
            if (!IsSignIn(context.ActionDescriptor as ControllerActionDescriptor, req)) return;
            string user = UserNameOf(req);
            context.HttpContext.Items[KeyUser] = user;
            int minutes = _throttle.MinutesBlocked(user, Ip(context.HttpContext));
            if (minutes > 0)
            {
                var action = context.ActionDescriptor as ControllerActionDescriptor;
                if (action != null && !string.Equals(action.ActionName, "Login", StringComparison.OrdinalIgnoreCase))
                {
                    // called by the application / a tool, not by the sign-in page: answer in the application's own JSON shape
                    context.Result = new ContentResult
                    {
                        StatusCode = 429,
                        ContentType = "application/json; charset=utf-8",
                        Content = "{\"Status\":\"Failed\",\"ErrorMessage\":\"Too many wrong attempts. Please wait about " + minutes + " minute(s) and try again.\"}"
                    };
                    return;
                }
                context.Result = new ContentResult
                {
                    StatusCode = 429,
                    ContentType = "text/html; charset=utf-8",
                    Content = "<html><body style=\"font-family:sans-serif;margin:60px auto;max-width:520px\"><h2>Too many wrong sign-in attempts</h2>" +
                              "<p>For safety, sign-in is paused for about " + minutes + " minute(s). Please wait and try again, or ask the administrator.</p>" +
                              "<p><a href=\"/Account/Login\">Back to the sign-in page</a></p></body></html>"
                };
            }
        }

        public void OnResultExecuting(ResultExecutingContext context)
        {
            var http = context.HttpContext;
            if (!IsSignIn(context.ActionDescriptor as ControllerActionDescriptor, http.Request) || !http.Items.ContainsKey(KeyUser)) return;
            string user = Convert.ToString(http.Items[KeyUser]);
            string ip = Ip(http);

            var view = context.Result as ViewResult;
            var json = context.Result as JsonResult;
            string jsonText = json == null ? null : json.Value as string;      // the change-password call answers {"Status":"OK"|"Failed",...}
            bool failed = (view != null && Convert.ToString(view.ViewData["status"]) == "login-failed") || context.Result is UnauthorizedResult
                          || (jsonText != null && StatusFailed.IsMatch(jsonText));
            bool ok = context.Result is RedirectToActionResult || context.Result is OkObjectResult
                      || (jsonText != null && StatusOk.IsMatch(jsonText));
            if (failed) _throttle.Failed(user, ip);
            else if (ok) _throttle.Succeeded(user, ip);
        }

        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
