using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlagiarismCheckerPlus
{
    /// <summary>
    /// Verifies webhook deliveries. Every delivery carries
    /// <c>PCP-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;")&gt;</c>.
    /// </summary>
    public static class Webhook
    {
        /// <summary>Header that carries the signature.</summary>
        public const string SignatureHeader = "PCP-Signature";

        /// <summary>How old a signed timestamp may be before it is rejected (300 seconds).</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

        /// <summary>Checks the header against the raw body (UTF-8 text, before JSON parsing).</summary>
        public static bool VerifySignature(string secret, string? header, string body, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            if (body == null) return false;
            return VerifySignature(secret, header, Encoding.UTF8.GetBytes(body), tolerance, now);
        }

        /// <summary>
        /// Checks a PCP-Signature header against the raw body bytes. Returns false for a missing or
        /// malformed header, a wrong signature, or a timestamp more than <paramref name="tolerance"/>
        /// (default 300 seconds) from <paramref name="now"/> (default the current time).
        /// Signatures are compared in constant time.
        /// </summary>
        public static bool VerifySignature(string secret, string? header, byte[] body, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header) || body == null) return false;

            long? timestamp = null;
            var signatures = new List<byte[]>();
            foreach (var part in header!.Split(','))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var key = part.Substring(0, eq).Trim();
                var value = part.Substring(eq + 1).Trim();
                if (key == "t" && value.Length > 0 && value.Length <= 18 && IsDigits(value))
                {
                    timestamp = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
                }
                else if (key == "v1")
                {
                    var sig = DecodeHex(value);
                    if (sig != null) signatures.Add(sig);
                }
            }
            if (timestamp == null || signatures.Count == 0) return false;

            var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var limit = (long)(tolerance ?? DefaultTolerance).TotalSeconds;
            if (Math.Abs(current - timestamp.Value) > limit) return false;

            byte[] expected;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var prefix = Encoding.UTF8.GetBytes(timestamp.Value.ToString(CultureInfo.InvariantCulture) + ".");
                var message = new byte[prefix.Length + body.Length];
                Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
                Buffer.BlockCopy(body, 0, message, prefix.Length, body.Length);
                expected = hmac.ComputeHash(message);
            }
            var ok = false;
            foreach (var sig in signatures) ok |= FixedTimeEquals(sig, expected);
            return ok;
        }

        private static bool IsDigits(string value)
        {
            foreach (var c in value) if (c < '0' || c > '9') return false;
            return true;
        }

        private static byte[]? DecodeHex(string value)
        {
            if (value.Length == 0 || value.Length % 2 != 0) return null;
            var bytes = new byte[value.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var hi = HexValue(value[2 * i]);
                var lo = HexValue(value[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
