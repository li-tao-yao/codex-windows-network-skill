using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProxyCheck {
    public class Result {
        public string Manual, Endpoint, AutoDetect, PAC, MatchingBypass;
        public int Error;
    }
    public static class Native {
        [StructLayout(LayoutKind.Sequential)]
        struct IEConfig { public int Detect; public IntPtr Pac, Proxy, Bypass; }
        [StructLayout(LayoutKind.Explicit)]
        struct OptionValue {
            [FieldOffset(0)] public int Flags;
            [FieldOffset(0)] public IntPtr Text;
            [FieldOffset(0)] public System.Runtime.InteropServices.ComTypes.FILETIME Time;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Option { public int Id; public OptionValue Value; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        struct OptionList {
            public int Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Connection;
            public int Count, Error;
            public IntPtr Options;
        }
        [DllImport("winhttp.dll", SetLastError=true)]
        static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IEConfig data);
        [DllImport("wininet.dll", SetLastError=true, CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern bool InternetQueryOptionW(IntPtr handle, int option, ref OptionList list, ref int length);
        [DllImport("wininet.dll", SetLastError=true, CharSet=CharSet.Unicode, ExactSpelling=true)]
        static extern bool InternetGetConnectedStateExW(out int flags, StringBuilder name, int length, int reserved);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr p);
        static bool HasText(IntPtr p) { return p != IntPtr.Zero && Marshal.ReadInt16(p) != 0; }
        static void FreeText(IntPtr p) { if (p != IntPtr.Zero) GlobalFree(p); }
        static string Bit(bool on) { return on ? "on" : "off"; }

        // Report only a loopback port. A remote address, PAC URL or credential is never printed.
        static string Endpoint(IntPtr p) {
            return HasText(p) ? DescribeEndpoint(Marshal.PtrToStringUni(p)) : "-";
        }
        public static string DescribeEndpoint(string value) {
            if (String.IsNullOrWhiteSpace(value)) return "-";
            var labels = new List<string>();
            foreach (string part in value.Split(';')) {
                if (part.Trim().Length == 0) continue;
                var m = Regex.Match(part.Trim(), @"^(?:(http|https|socks|ftp)=)?(?:[a-z0-9]+://)?(127\.0\.0\.1|localhost|\[::1\]):([0-9]{1,5})/?$", RegexOptions.IgnoreCase);
                if (!m.Success) { labels.Add("OTHER"); continue; }
                var protocol = m.Groups[1].Success ? m.Groups[1].Value + "=" : "";
                labels.Add(protocol + "loopback:" + m.Groups[3].Value);
            }
            return String.Join(";", labels.ToArray());
        }
        // Only test basic host wildcards against the two public HTTPS destinations.
        // PAC decisions are reported separately by the per-URL lookup below.
        static string MatchingBypass(IntPtr p) {
            if (!HasText(p)) return "-";
            var labels = new List<string>();
            string rules = Marshal.PtrToStringUni(p);
            foreach (string host in new [] { "www.youtube.com", "chatgpt.com" }) {
                foreach (string item in Regex.Split(rules, @"[;,\s]+")) {
                    string rule = item.Trim();
                    if (rule.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rule = rule.Substring(8);
                    if (rule.EndsWith(":443", StringComparison.Ordinal)) rule = rule.Substring(0, rule.Length - 4);
                    if (rule.StartsWith(".", StringComparison.Ordinal)) rule = "*" + rule;
                    if (rule.Length == 0 || rule.Length > 512 || rule.Contains("/") || rule.StartsWith("<")) continue;
                    string pattern = "^" + Regex.Escape(rule).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                    if (Regex.IsMatch(host, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) {
                        labels.Add(host);
                        break;
                    }
                }
            }
            return labels.Count == 0 ? "-" : String.Join(",", labels.ToArray());
        }
        public static string ActiveName() {
            int flags;
            var name = new StringBuilder(1024);
            return InternetGetConnectedStateExW(out flags, name, name.Capacity, 0) ? name.ToString() : "";
        }
        public static Result Current() {
            IEConfig c;
            if (!WinHttpGetIEProxyConfigForCurrentUser(out c))
                return new Result { Error=Marshal.GetLastWin32Error() };
            try {
                return new Result { Manual=Bit(HasText(c.Proxy)), Endpoint=Endpoint(c.Proxy), AutoDetect=Bit(c.Detect != 0), PAC=Bit(HasText(c.Pac)), MatchingBypass=MatchingBypass(c.Bypass) };
            } finally {
                FreeText(c.Pac); FreeText(c.Proxy); FreeText(c.Bypass);
            }
        }
        public static Result Profile(string name) {
            int stride = Marshal.SizeOf(typeof(Option));
            // Query flags, proxy server and proxy bypass rules.
            var list = new OptionList { Size=Marshal.SizeOf(typeof(OptionList)), Connection=name, Count=3, Options=Marshal.AllocHGlobal(3*stride) };
            IntPtr second = IntPtr.Add(list.Options, stride);
            IntPtr third = IntPtr.Add(list.Options, 2*stride);
            bool read = false;
            try {
                Marshal.StructureToPtr(new Option {Id=1}, list.Options, false);
                Marshal.StructureToPtr(new Option {Id=2}, second, false);
                Marshal.StructureToPtr(new Option {Id=3}, third, false);
                int length = list.Size;
                read = InternetQueryOptionW(IntPtr.Zero, 75, ref list, ref length);
                if (!read) return new Result {Error=Marshal.GetLastWin32Error()};
                int flags = ((Option)Marshal.PtrToStructure(list.Options, typeof(Option))).Value.Flags;
                var server = ((Option)Marshal.PtrToStructure(second, typeof(Option))).Value.Text;
                var bypass = ((Option)Marshal.PtrToStructure(third, typeof(Option))).Value.Text;
                return new Result {Manual=Bit((flags & 2) != 0), Endpoint=Endpoint(server), AutoDetect=Bit((flags & 8) != 0), PAC=Bit((flags & 4) != 0), MatchingBypass=MatchingBypass(bypass)};
            } finally {
                if (read) {
                    FreeText(((Option)Marshal.PtrToStructure(second, typeof(Option))).Value.Text);
                    FreeText(((Option)Marshal.PtrToStructure(third, typeof(Option))).Value.Text);
                }
                Marshal.FreeHGlobal(list.Options);
            }
        }
        public static Result Lan() { return Profile(null); }
    }
    internal static class Program {
        const string InternetSettings = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";
        const string InternetPolicy = @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";
        static readonly string[] EnvNames = { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
        static readonly string[] PolicyNames = { "ProxySettings", "ProxyMode", "ProxyServer", "ProxyPacUrl", "ProxyBypassList", "ProxyServerMode" };
        static readonly string[] AllowedModes = { "direct", "system", "fixed_servers", "pac_script", "auto_detect" };

        static void Show(string title, Result item) {
            if (item.Error != 0) {
                Console.WriteLine("{0}: Win32 error {1}", title, item.Error);
                return;
            }
            Console.WriteLine("{0}: manual={1} endpoint={2} autodetect={3} PAC={4} matching-host-rules={5}",
                title, item.Manual, item.Endpoint, item.AutoDetect, item.PAC, item.MatchingBypass);
        }
        static void ShowRegistry(RegistryKey hive, string label) {
            using (var key = hive.OpenSubKey(InternetSettings)) {
                if (key == null) { Console.WriteLine("{0}: no key", label); return; }
                object enabled = key.GetValue("ProxyEnable");
                string endpoint = Native.DescribeEndpoint(key.GetValue("ProxyServer") as string);
                Console.WriteLine("{0}: ProxyEnable={1} endpoint={2}", label, enabled ?? "(unset)", endpoint);
            }
        }
        static string KnownMode(object value) {
            var mode = value as string;
            return AllowedModes.Contains(mode) ? mode : "(unspecified)";
        }
        static bool ShowBrowserPolicy(RegistryKey hive, string hiveLabel, string browser, string suffix) {
            using (var key = hive.OpenSubKey(@"SOFTWARE\Policies\" + browser + suffix)) {
                if (key == null) return false;
                var names = key.GetValueNames().Where(n => PolicyNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                string mode = KnownMode(key.GetValue("ProxyMode"));
                if (names.Contains("ProxySettings", StringComparer.OrdinalIgnoreCase)) {
                    var json = key.GetValue("ProxySettings") as string;
                    if (!String.IsNullOrEmpty(json)) {
                        var match = Regex.Match(json, @"""ProxyMode""\s*:\s*""(direct|system|fixed_servers|pac_script|auto_detect)""", RegexOptions.IgnoreCase);
                        if (match.Success) mode = KnownMode(match.Groups[1].Value.ToLowerInvariant());
                    }
                }
                using (var nested = key.OpenSubKey("ProxySettings")) {
                    if (nested != null) { names.Add("ProxySettings(subkey)"); mode = KnownMode(nested.GetValue("ProxyMode")); }
                }
                if (names.Count == 0) return false;
                Console.WriteLine("{0} {1}{2} policy names: {3}; mode={4}", hiveLabel, browser, suffix, String.Join(",", names), mode);
                return true;
            }
        }
        // The known layout stores three length-prefixed byte fields following the flags.
        // Leave any trailing, version-specific bytes opaque.
        static bool HasBoundedFields(byte[] data) {
            if (data == null || data.Length < 16) return false;
            int pos = 12;
            for (int field = 0; field < 3; field++) {
                if (data.Length - pos < 4) return false;
                uint length = BitConverter.ToUInt32(data, pos);
                pos += 4;
                if (length > data.Length - pos) return false;
                pos += (int)length;
            }
            return true;
        }
        static void ShowRecord(RegistryKey key, string name, string label) {
            if (key.GetValueKind(name) != RegistryValueKind.Binary) {
                Console.WriteLine("{0}: type is not REG_BINARY", label); return;
            }
            var data = key.GetValue(name) as byte[];
            if (!HasBoundedFields(data)) { Console.WriteLine("{0}: unrecognized record layout", label); return; }
            uint flags = BitConverter.ToUInt32(data, 8);
            int count = (int)BitConverter.ToUInt32(data, 12);
            Console.WriteLine("{0}: manual={1} endpoint={2}", label, (flags & 2) != 0 ? "on" : "off",
                Native.DescribeEndpoint(Encoding.ASCII.GetString(data, 16, count)));
        }
        static void CompareDialupNames(string name, RegistryView view) {
            var strictUtf8 = new UTF8Encoding(false, true);
            var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            string alternate;
            try {
                alternate = gbk.GetString(strictUtf8.GetBytes(name));
                if (strictUtf8.GetString(gbk.GetBytes(alternate)) != name) return;
            } catch (ArgumentException) {
                Console.WriteLine("{0}: name has no reversible UTF-8/GBK candidate", view);
                return;
            }
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
            using (var key = hive.OpenSubKey(InternetSettings + @"\Connections")) {
                if (key == null) { Console.WriteLine("{0}: no Connections key", view); return; }
                var names = key.GetValueNames();
                bool exact = names.Contains(name, StringComparer.OrdinalIgnoreCase);
                bool distinct = !String.Equals(name, alternate, StringComparison.OrdinalIgnoreCase);
                bool candidate = distinct && names.Contains(alternate, StringComparer.OrdinalIgnoreCase);
                Console.WriteLine("{0}: correct-name={1}; distinct UTF8-as-GBK candidate={2}", view, exact, candidate);
                if (exact) ShowRecord(key, name, "Correct-name record");
                if (!candidate) return;
                ShowRecord(key, alternate, "Candidate record");
                if (!names.Contains("DefaultConnectionSettings", StringComparer.OrdinalIgnoreCase) ||
                    key.GetValueKind(alternate) != RegistryValueKind.Binary ||
                    key.GetValueKind("DefaultConnectionSettings") != RegistryValueKind.Binary) {
                    Console.WriteLine("Candidate/default missing or not both REG_BINARY."); return;
                }
                var data = key.GetValue(alternate) as byte[];
                var baseline = key.GetValue("DefaultConnectionSettings") as byte[];
                if (!HasBoundedFields(data) || !HasBoundedFields(baseline)) {
                    Console.WriteLine("Candidate/default length fields do not fit known layout."); return;
                }
                uint length = BitConverter.ToUInt32(data, 12);
                var server = Native.DescribeEndpoint(Encoding.ASCII.GetString(data, 16, (int)length));
                Console.WriteLine("Both REG_BINARY, three fields bounded; bytes from offset 8 equal={0}; candidate flags={1}; candidate endpoint={2}",
                    data.Skip(8).SequenceEqual(baseline.Skip(8)), BitConverter.ToUInt32(data, 8), server);
            }
        }
        internal static string LifecycleFinding(string expect, Result current, bool listening, int port, int? userSwitch) {
            if (current.PAC == "on" || current.AutoDetect == "on")
                return "Automatic proxy selection is enabled. Compare per-URL routes and real browsing.";
            string desired = "loopback:" + port;
            var endpoints = current.Endpoint.Split(';');
            bool local = current.Manual == "on" && endpoints.Any(part => part.Split('=').Last() == desired);
            if (expect == "off" && local)
                return "OFF mismatch: active connection still advertises the local proxy. Compare the normal browser with an explicit direct GET.";
            if (local && userSwitch == 0)
                return "Switch mismatch: HKCU ProxyEnable is off while the active connection advertises the local proxy. Check which source follows the actual client switch.";
            if (local && !listening)
                return "No listener in this sample for the active local proxy. Recheck after startup or reload and confirm the per-URL route.";
            if (expect == "off" && current.Manual == "off")
                return listening ? "Active manual proxy is off; the local core still listens. Verify the normal browser on a direct site."
                    : "Active manual proxy is off; the port has no listener. Verify the normal browser on a direct site.";
            if (expect == "on" && !local)
                return "ON mismatch: expected manual local proxy is not ready on this connection.";
            return "Compare per-URL routes and actual browsing with the intended state.";
        }
        static void Report(int port, string requestedName, string expect) {
            var ppp = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ppp && n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string active = Native.ActiveName();
            Console.WriteLine("Active PPP count: {0}; WinINet active name matches PPP: {1}", ppp.Count,
                ppp.Contains(active, StringComparer.OrdinalIgnoreCase));
            bool listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port &&
                (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)));
            Console.WriteLine("Local TCP listener {0}: {1}", port, listening);
            Show("LAN/default (WinINet)", Native.Lan());
            var entries = new List<string>(ppp);
            if (!String.IsNullOrEmpty(requestedName) && !entries.Contains(requestedName, StringComparer.OrdinalIgnoreCase)) entries.Add(requestedName);
            int entryNumber = 0;
            foreach (string entry in entries) {
                bool live = ppp.Contains(entry, StringComparer.OrdinalIgnoreCase);
                string label = live ? "Up PPP" : "Requested entry (not an active PPP)";
                Show(String.Format("{0} #{1} WinINet active-name-match={2}", label, ++entryNumber,
                    String.Equals(entry, active, StringComparison.OrdinalIgnoreCase)), Native.Profile(entry));
                CompareDialupNames(entry, RegistryView.Registry32);
                if (Environment.Is64BitOperatingSystem) CompareDialupNames(entry, RegistryView.Registry64);
            }
            var current = Native.Current();
            Show("Current user / active connection (WinHTTP IE API)", current);
            using (var settings = Registry.CurrentUser.OpenSubKey(InternetSettings)) {
                int? userSwitch = settings == null ? null : settings.GetValue("ProxyEnable") as int?;
                if (current.Error == 0)
                    Console.WriteLine("State {0}: {1}", expect, LifecycleFinding(expect, current, listening, port, userSwitch));
            }
            var selector = WebRequest.GetSystemWebProxy();
            foreach (var host in new [] { "www.youtube.com", "chatgpt.com" }) {
                var uri = new Uri("https://" + host + "/");
                try {
                    var route = selector.GetProxy(uri);
                    string choice = route == null || route.Equals(uri) ? "DIRECT" :
                        (route.IsLoopback ? "loopback:" + route.Port : "OTHER");
                    Console.WriteLine("Effective .NET route for {0}: {1}", host, choice);
                } catch { Console.WriteLine("Effective .NET route for {0}: lookup error", host); }
            }
            ShowRegistry(Registry.CurrentUser, "HKCU user values");
            using (var key = Registry.LocalMachine.OpenSubKey(InternetPolicy)) {
                object scope = key == null ? null : key.GetValue("ProxySettingsPerUser");
                bool machine = scope is int && (int)scope == 0;
                Console.WriteLine("Windows proxy policy scope: {0}", machine ? "machine" : (scope == null ? "per user (default)" : "per user"));
                if (machine) ShowRegistry(Registry.LocalMachine, "HKLM machine values");
            }
            bool hasPolicy = false;
            foreach (var browser in new [] { @"Google\Chrome", @"Microsoft\Edge" }) {
                foreach (var suffix in new [] { "", @"\Recommended" }) {
                    hasPolicy |= ShowBrowserPolicy(Registry.CurrentUser, "HKCU", browser, suffix);
                    hasPolicy |= ShowBrowserPolicy(Registry.LocalMachine, "HKLM", browser, suffix);
                }
            }
            if (!hasPolicy) Console.WriteLine("Chrome/Edge registry proxy policy: none found");
            foreach (EnvironmentVariableTarget scope in new [] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }) {
                var names = EnvNames.Where(name => Environment.GetEnvironmentVariable(name, scope) != null).ToArray();
                Console.WriteLine("{0} proxy variable names: {1}", scope, names.Length == 0 ? "(none)" : String.Join(",", names));
            }
        }
        public static int Main(string[] args) {
            int port = 10808;
            string name = null;
            string expect = "observe";
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--no-pause") continue;
                if (args[i] == "--expect" && i + 1 < args.Length && (args[i+1]=="on" || args[i+1]=="off")) { expect=args[++i]; continue; }
                if (args[i] == "--dialup" && i + 1 < args.Length) { name = args[++i]; continue; }
                if (args[i] == "--port" && i + 1 < args.Length && Int32.TryParse(args[++i], out port) && port >= 1 && port <= 65535) continue;
                Console.WriteLine("Usage: Check-WindowsProxy.exe [--port PORT] [--expect on|off] [--dialup NAME] [--no-pause]");
                return args.Contains("--help") ? 0 : 2;
            }
            int result = 0;
            try { Report(port, name, expect); }
            catch (Exception e) {
                Console.WriteLine("Check incomplete: {0}; HRESULT 0x{1:X8}", e.GetType().Name, e.HResult);
                result = 1;
            }
            if (!Console.IsInputRedirected && !args.Contains("--no-pause")) {
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
            return result;
        }
    }
}
