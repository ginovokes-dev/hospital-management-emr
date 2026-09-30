using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using DanpheEMR.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DanpheEMR.CareTeam
{
    /// <summary>
    /// Brings the EMR database up to what this version of the application needs (care teams, messages, menu entries for the doctor
    /// screens) every time the application starts. The scripts are the ones in Database/3. Doctor-Care-Team - embedded in the
    /// application - and are safe to run again and again.
    ///
    /// Settings (environment variables):
    ///   DANPHE_DB_UPGRADE=0         do not touch the database at all
    ///   DANPHE_ADMIN_PASSWORD=...   first start only: password of the 'admin' login (and the licence end date is moved to
    ///                               DANPHE_LICENSE_END, default 2099-12-31). Done once - a password the admin changes later stays.
    ///   DANPHE_FRESH_START=1        first start only: switch off the sample staff logins that ship with the sample database
    ///   DANPHE_FILES_DIR=/data/files  Linux only: folder (a Docker volume) that uploads are kept in; the settings that name a Windows drive
    ///                               in the sample database are pointed at folders below it
    /// </summary>
    public static class DatabaseUpgrader
    {
        private static readonly Regex GoLine = new Regex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsFalse(string v)
        {
            return v != null && (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTrue(string v)
        {
            return v != null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        public static void Run(string connectionString, IConfiguration config, ILogger log)
        {
            if (IsFalse(config["DANPHE_DB_UPGRADE"]))
            {
                log.LogInformation("Database upgrade switched off (DANPHE_DB_UPGRADE=0).");
                return;
            }
            try
            {
                if (!WaitForDatabase(connectionString, TimeSpan.FromMinutes(5), log)) return;

                using (var cn = new SqlConnection(connectionString))
                {
                    cn.Open();
                    Apply(cn, "01_care_team_schema.sql", null, log);
                    Apply(cn, "02_care_team_access.sql", null, log);
                    Apply(cn, "05_attachments_without_filestream.sql", null, log);   // Linux/Mac SQL Server: uploads of scans and documents
                    Apply(cn, "06_not_demo_mode.sql", null, log);                    // the sample database is marked "demo": that hides the Change Password button

                    // Docker / Linux: the sample database keeps its uploads in folders on a Windows drive; point them at the data folder instead
                    string filesDir = config["DANPHE_FILES_DIR"];
                    if (!string.IsNullOrWhiteSpace(filesDir) && Path.DirectorySeparatorChar == '/')
                        Apply(cn, "07_file_folders_on_linux.sql", new Dictionary<string, string> { { "FILES_DIR", filesDir.Trim().TrimEnd('/') } }, log);

                    string adminPassword = config["DANPHE_ADMIN_PASSWORD"];
                    if (!string.IsNullOrEmpty(adminPassword))
                    {
                        string licenceEnd = config["DANPHE_LICENSE_END"];
                        if (string.IsNullOrWhiteSpace(licenceEnd)) licenceEnd = "2099-12-31";
                        Apply(cn, "03_login_and_license.sql", new Dictionary<string, string>
                        {
                            { "ADMIN_PASSWORD_ENC", RBAC.EncryptPassword(adminPassword) },
                            { "LICENSE_END_ENC", RBAC.EncryptPassword(licenceEnd.Trim()) }
                        }, log);
                    }
                    if (IsTrue(config["DANPHE_FRESH_START"]))
                        Apply(cn, "04_fresh_start.sql", null, log);
                }
                log.LogInformation("Database is up to date.");
            }
            catch (Exception ex)
            {
                // the application still starts - the doctor screens simply will not work until this is fixed
                log.LogError(0, ex, "The database upgrade failed: " + ex.Message);
            }
        }

        private static bool WaitForDatabase(string connectionString, TimeSpan patience, ILogger log)
        {
            DateTime giveUp = DateTime.UtcNow + patience;
            string lastError = null;
            while (true)
            {
                try
                {
                    using (var cn = new SqlConnection(connectionString))
                    {
                        cn.Open();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
                if (DateTime.UtcNow > giveUp)
                {
                    log.LogError("The database did not become available: " + lastError);
                    return false;
                }
                log.LogInformation("Waiting for the database ... (" + lastError + ")");
                Thread.Sleep(3000);
            }
        }

        private static string ReadScript(string fileName)
        {
            var asm = typeof(DatabaseUpgrader).Assembly;
            string resource = "DatabaseScripts." + fileName;
            using (Stream s = asm.GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException("Embedded database script not found: " + resource);
                using (var rd = new StreamReader(s, System.Text.Encoding.UTF8))
                    return rd.ReadToEnd();
            }
        }

        private static void Apply(SqlConnection cn, string fileName, Dictionary<string, string> variables, ILogger log)
        {
            string script = ReadScript(fileName);
            if (variables != null)
                foreach (var kv in variables)
                    script = script.Replace("$(" + kv.Key + ")", kv.Value.Replace("'", "''"));

            foreach (string batch in GoLine.Split(script))
            {
                string sql = batch.Trim();
                if (sql.Length == 0) continue;
                using (var cmd = new SqlCommand(sql, cn))
                {
                    cmd.CommandTimeout = 180;
                    cmd.ExecuteNonQuery();
                }
            }
            log.LogInformation("Applied database script " + fileName);
        }
    }
}
