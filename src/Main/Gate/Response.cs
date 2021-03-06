﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Gate.Utils;
using System.Text;
using System.IO;
using System.Threading;

namespace Gate
{
    // A helper object for creating, modifying, or consuming response data in the Environment dictionary.
    public struct Response
    {
        public Response(IDictionary<string, object> environment)
            : this()
        {
            this.Environment = environment;
            this.Encoding = Encoding.UTF8;
        }

        public IDictionary<string, object> Environment { get; set; }

        public IDictionary<string, string[]> Headers
        {
            get { return Environment.Get<IDictionary<string, string[]>>(OwinConstants.ResponseHeaders); }
            set { Environment.Set<IDictionary<string, string[]>>(OwinConstants.ResponseHeaders, value); }
        }

        public string Status
        {
            get
            {
                var reasonPhrase = ReasonPhrase;
                return string.IsNullOrEmpty(reasonPhrase)
                    ? StatusCode.ToString(CultureInfo.InvariantCulture)
                    : StatusCode.ToString(CultureInfo.InvariantCulture) + " " + reasonPhrase;
            }
            set
            {
                if (value.Length < 3 || (value.Length >= 4 && value[3] != ' '))
                {
                    throw new ArgumentException("Status must be a string with 3 digit statuscode, a space, and a reason phrase");
                }
                StatusCode = int.Parse(value.Substring(0, 3));
                ReasonPhrase = value.Length < 4 ? null : value.Substring(4);
            }
        }

        public int StatusCode
        {
            get { return Environment.Get<int>(OwinConstants.ResponseStatusCode); }
            set { Environment.Set<int>(OwinConstants.ResponseStatusCode, value); }
        }

        public string ReasonPhrase
        {
            get
            {
                string reasonPhrase = Environment.Get<string>(OwinConstants.ResponseReasonPhrase);
                return string.IsNullOrEmpty(reasonPhrase) ? ReasonPhrases.ToReasonPhrase(StatusCode) : reasonPhrase;
            }
            set { Environment.Set<string>(OwinConstants.ResponseReasonPhrase, value); }
        }

        public string GetHeader(string name)
        {
            var values = GetHeaders(name);
            if (values == null)
            {
                return null;
            }

            switch (values.Length)
            {
                case 0:
                    return string.Empty;
                case 1:
                    return values[0];
                default:
                    return string.Join(",", values);
            }
        }

        public string[] GetHeaders(string name)
        {
            string[] existingValues;
            return Headers.TryGetValue(name, out existingValues) ? existingValues : null;
        }

        public Response SetHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                Headers.Remove(value);
            else
                Headers[name] = new[] { value };
            return this;
        }

        void OnSendingHeaders(Action<object> callback, object state)
        {
            var register = Environment.Get<Action<Action<object>, object>>(OwinConstants.OnSendingHeaders);
            if (register == null)
            {
                throw new NotImplementedException("Environment does not contain server.OnSendingHeaders");
            }
            register(callback, state);
        }

        public Response SetCookie(string key, string value)
        {
            Headers.AddHeader("Set-Cookie", Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value) + "; path=/");
            return this;
        }

        public Response SetCookie(string key, Cookie cookie)
        {
            var domainHasValue = !string.IsNullOrEmpty(cookie.Domain);
            var pathHasValue = !string.IsNullOrEmpty(cookie.Path);
            var expiresHasValue = cookie.Expires.HasValue;

            var setCookieValue = string.Concat(
                Uri.EscapeDataString(key),
                "=",
                Uri.EscapeDataString(cookie.Value ?? ""),
                !domainHasValue ? null : "; domain=",
                !domainHasValue ? null : cookie.Domain,
                !pathHasValue ? null : "; path=",
                !pathHasValue ? null : cookie.Path,
                !expiresHasValue ? null : "; expires=",
                !expiresHasValue ? null : cookie.Expires.Value.ToString("ddd, dd-MMM-yyyy HH:mm:ss ") + "GMT",
                !cookie.Secure ? null : "; secure",
                !cookie.HttpOnly ? null : "; HttpOnly"
                );
            Headers.AddHeader("Set-Cookie", setCookieValue);
            return this;
        }

        public Response DeleteCookie(string key)
        {
            Func<string, bool> predicate = value => value.StartsWith(key + "=", StringComparison.InvariantCultureIgnoreCase);

            var deleteCookies = new[] { Uri.EscapeDataString(key) + "=; expires=Thu, 01-Jan-1970 00:00:00 GMT" };
            var existingValues = Headers.GetHeaders("Set-Cookie");
            if (existingValues == null)
            {
                Headers["Set-Cookie"] = deleteCookies;
                return this;
            }

            Headers["Set-Cookie"] = existingValues.Where(value => !predicate(value)).Concat(deleteCookies).ToArray();
            return this;
        }

        public Response DeleteCookie(string key, Cookie cookie)
        {
            var domainHasValue = !string.IsNullOrEmpty(cookie.Domain);
            var pathHasValue = !string.IsNullOrEmpty(cookie.Path);

            Func<string, bool> rejectPredicate;
            if (domainHasValue)
            {
                rejectPredicate = value =>
                    value.StartsWith(key + "=", StringComparison.InvariantCultureIgnoreCase) &&
                        value.IndexOf("domain=" + cookie.Domain, StringComparison.InvariantCultureIgnoreCase) != -1;
            }
            else if (pathHasValue)
            {
                rejectPredicate = value =>
                    value.StartsWith(key + "=", StringComparison.InvariantCultureIgnoreCase) &&
                        value.IndexOf("path=" + cookie.Path, StringComparison.InvariantCultureIgnoreCase) != -1;
            }
            else
            {
                rejectPredicate = value => value.StartsWith(key + "=", StringComparison.InvariantCultureIgnoreCase);
            }
            var existingValues = Headers.GetHeaders("Set-Cookie");
            if (existingValues != null)
            {
                Headers["Set-Cookie"] = existingValues.Where(value => !rejectPredicate(value)).ToArray();
            }

            return SetCookie(key, new Cookie
            {
                Path = cookie.Path,
                Domain = cookie.Domain,
                Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        public class Cookie
        {
            public Cookie()
            {
                Path = "/";
            }
            public Cookie(string value)
            {
                Path = "/";
                Value = value;
            }
            public string Value { get; set; }
            public string Domain { get; set; }
            public string Path { get; set; }
            public DateTime? Expires { get; set; }
            public bool Secure { get; set; }
            public bool HttpOnly { get; set; }
        }

        public string ContentType
        {
            get { return GetHeader("Content-Type"); }
            set { SetHeader("Content-Type", value); }
        }

        public long ContentLength
        {
            get { return long.Parse(GetHeader("Content-Length"), CultureInfo.InvariantCulture); }
            set { SetHeader("Content-Length", value.ToString(CultureInfo.InvariantCulture)); }
        }

        public Encoding Encoding { get; set; }

        public Stream OutputStream
        {
            get { return Environment.Get<Stream>(OwinConstants.ResponseBody); }
            set { Environment.Set<Stream>(OwinConstants.ResponseBody, value); }
        }

        public void Write(string text)
        {
            var data = Encoding.GetBytes(text);
            Write(data);
        }

        public void Write(string format, params object[] args)
        {
            Write(string.Format(format, args));
        }

        public void Write(byte[] buffer)
        {
            OutputStream.Write(buffer, 0, buffer.Length);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            OutputStream.Write(buffer, offset, count);
        }

        public void Write(ArraySegment<byte> data)
        {
            OutputStream.Write(data.Array, data.Offset, data.Count);
        }

        public Task WriteAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            var data = Encoding.GetBytes(text);
            return WriteAsync(data, cancellationToken);
        }

        public Task WriteAsync(byte[] buffer, CancellationToken cancellationToken = default(CancellationToken))
        {
            return OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default(CancellationToken))
        {
            return OutputStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public Task WriteAsync(ArraySegment<byte> data, CancellationToken cancellationToken = default(CancellationToken))
        {
            return OutputStream.WriteAsync(data.Array, data.Offset, data.Count, cancellationToken);
        }
    }
}