using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace DanpheEMR.Utilities
{
    /// <summary>
    /// The application was written for Windows/IIS, where /Images/Logo.PNG finds the file logo.png. On Linux (and on a Mac that is
    /// formatted case-sensitively) it does not. When a static file is asked for with different letter-case than on disk, the request
    /// path is rewritten to the file's real spelling - but only if the exact spelling does not exist, so nothing changes where
    /// file names are not case-sensitive.
    /// </summary>
    public class CaseInsensitiveStaticFileMiddleware
    {
        private class DirListing
        {
            public DateTime WrittenUtc;
            public System.Collections.Generic.Dictionary<string, string> ByLowerName;
        }

        private readonly RequestDelegate _next;
        private readonly string _root;
        private readonly ConcurrentDictionary<string, DirListing> _listings = new ConcurrentDictionary<string, DirListing>();

        public CaseInsensitiveStaticFileMiddleware(RequestDelegate next, string webRootPath)
        {
            _next = next;
            _root = webRootPath;
        }

        public Task Invoke(HttpContext context)
        {
            var req = context.Request;
            if (!string.IsNullOrEmpty(_root) && (HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method)))
            {
                string path = req.Path.Value;
                if (!string.IsNullOrEmpty(path) && path.Length > 1 && Path.HasExtension(path) && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    string real = Resolve(path);
                    if (real != null && !string.Equals(real, path, StringComparison.Ordinal)) req.Path = new PathString(real);
                }
            }
            return _next(context);
        }

        /// <summary>the real spelling of a file under the web root, or null when the exact spelling exists or nothing matches</summary>
        private string Resolve(string urlPath)
        {
            try
            {
                string exact = Path.Combine(_root, urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(exact)) return null;

                string current = _root;
                var parts = urlPath.Split('/').Where(p => p.Length > 0).ToArray();
                var real = new string[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (part == "." || part == ".." || part.IndexOf('\\') >= 0 || part.IndexOf('\0') >= 0) return null;
                    string candidate = Path.Combine(current, part);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        real[i] = part;
                        current = candidate;
                        continue;
                    }
                    string hit = Lookup(current, part);
                    if (hit == null) return null;
                    real[i] = hit;
                    current = Path.Combine(current, hit);
                }
                return File.Exists(current) ? "/" + string.Join("/", real) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string Lookup(string directory, string name)
        {
            if (!Directory.Exists(directory)) return null;
            DateTime written = Directory.GetLastWriteTimeUtc(directory);
            DirListing listing;
            if (!_listings.TryGetValue(directory, out listing) || listing.WrittenUtc != written)
            {
                listing = new DirListing { WrittenUtc = written, ByLowerName = new System.Collections.Generic.Dictionary<string, string>() };
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    string n = Path.GetFileName(entry);
                    string lower = n.ToLowerInvariant();
                    if (!listing.ByLowerName.ContainsKey(lower)) listing.ByLowerName[lower] = n;
                }
                if (_listings.Count > 5000) _listings.Clear();
                _listings[directory] = listing;
            }
            string found;
            return listing.ByLowerName.TryGetValue(name.ToLowerInvariant(), out found) ? found : null;
        }
    }
}
