using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Windows .NET Framework 4.x. The installed desktop backend handles authentication and TOML edits.
namespace CodexReconnect {
    // Only messages constructed by this tool are suitable for sharing.
    internal sealed class RepairFailure : Exception {
        public RepairFailure(string message) : base(message) {}
    }

    internal class Core {
        const string Flag = "respect_system_proxy";
        internal delegate string Invoke(string arguments, int proxyPort);
        readonly Invoke call;
        readonly Action<string> say;
        readonly string userHome;
        readonly Dictionary<string,object> empty = new Dictionary<string,object>();
        public Core(Invoke invoke, Action<string> output, string expectedConfigHome) {
            call = invoke; say = output; userHome = expectedConfigHome;
        }
        static string Field(Dictionary<string,object> row, string key) {
            object v;
            if (!row.TryGetValue(key,out v)) return "";
            if (v is int || v is long) return Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture);
            return v as string ?? "";
        }
        Dictionary<string,object> ObjectAt(Dictionary<string,object> row, string key) {
            object v; return row.TryGetValue(key, out v) && v is Dictionary<string,object> ? (Dictionary<string,object>)v : empty;
        }
        Dictionary<string,object> Row(Dictionary<string,object> report, string id) {
            var map = ObjectAt(report, "checks");
            if (map.Count != 0) return ObjectAt(map,id);
            object rows;
            if (report.TryGetValue("checks",out rows) && rows is IEnumerable && !(rows is string))
                foreach (var item in ((IEnumerable)rows).OfType<Dictionary<string,object>>())
                    if (Field(item,"id")==id) return item;
            return empty;
        }
        sealed class Probe {
            public bool WebSocketGood, HttpGood, TransportFailure, ClosedEarly;
            public bool Good { get { return WebSocketGood && HttpGood; } }
            public string Config, Home, ProxySetting;
        }
        Probe Doctor(string label, string args, int port) {
            var json=call(args,port);
            Dictionary<string,object> report;
            try { report=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json); }
            catch(ArgumentException) { throw new RepairFailure("此版本的 doctor 未返回可识别的 JSON。"); }
            catch(InvalidOperationException) { throw new RepairFailure("此版本的 doctor 报告格式无法读取。"); }
            if (report == null) throw new RepairFailure("doctor 返回空报告");
            var ws = Row(report,"network.websocket_reachability");
            var http = Row(report,"network.provider_reachability");
            var details = ObjectAt(ws,"details");
            string status = Field(ws,"status"), summary = Field(ws,"summary"), handshake = Field(details,"handshake result");
            bool closedEarly=Regex.IsMatch(summary,@"closed (?:immediately )?after (?:the )?handshake|closed .*before response\.completed",RegexOptions.IgnoreCase)
                || Regex.IsMatch(Field(details,"immediate close code"),@"\b[1-4]\d{3}\b");
            bool good = !closedEarly && status=="ok" && (handshake.IndexOf("101",StringComparison.Ordinal)>=0 ||
                summary.IndexOf("handshake succeeded",StringComparison.OrdinalIgnoreCase)>=0);
            string evidence=String.Join(" ",details.Values.Select(Convert.ToString).Concat(new[]{summary}));
            if(Regex.IsMatch(Field(http,"status"),@"^(fail|error|warn|warning)$",RegexOptions.IgnoreCase))
                evidence+=" "+Field(http,"summary")+" "+String.Join(" ",ObjectAt(http,"details").Values.Select(Convert.ToString));
            bool failed = !closedEarly && Regex.IsMatch(status,@"^(warning|warn|fail|error)$",RegexOptions.IgnoreCase)
                && Regex.IsMatch(summary+" "+handshake,@"timed?\s*out|timeout|connect|handshake|websocket failed",RegexOptions.IgnoreCase)
                && !Regex.IsMatch(evidence,@"\b(401|403|407|429)\b|not support|unsupported|not configured|sign.in required|unauthori|rate limit|invalid request|access program not enabled",RegexOptions.IgnoreCase);
            object duration;
            say(String.Format("{0}：HTTP={1}，WebSocket={2}，握手={3}，耗时={4} ms",
                label,StatusLabel(http),StatusLabel(ws),closedEarly ? "已升级后断开" : good ? "成功" : failed ? "传输失败" : "查错误原文",
                ws.TryGetValue("durationMs",out duration) && (duration is int || duration is long) ? Convert.ToString(duration) : "?"));
            var cfg=ObjectAt(Row(report,"config.load"),"details");
            return new Probe {WebSocketGood=good, HttpGood=Field(http,"status")=="ok", TransportFailure=failed, ClosedEarly=closedEarly, Config=Field(cfg,"config.toml"),
                Home=Field(cfg,"CODEX_HOME"), ProxySetting=Field(ObjectAt(Row(report,"network.env"),"details"),"respect system proxy")};
        }
        static string StatusLabel(Dictionary<string,object> row) {
            string status=Field(row,"status");
            return Regex.IsMatch(status,@"\A(?:ok|warn|warning|fail|error|failed|skip|skipped)\z") ? status : "未知";
        }
        int Partial() {
            say("部分通过：WebSocket 握手已通过，服务 HTTP 尚未通过或缺少报告。请核对服务 HTTP，并从原工作空间新建聊天验收。");
            return 5;
        }
        string Effective() {
            var m=Regex.Match(call("features list",0),@"(?im)^[ \t]*respect_system_proxy[ \t]+[^\r\n]+?[ \t]+(true|false)[ \t]*\r?$");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "unknown";
        }
        internal static byte[] HashFile(string file) {
            if (!File.Exists(file)) return null;
            using(var stream=File.OpenRead(file)) using(var hash=SHA256.Create()) return hash.ComputeHash(stream);
        }
        static bool Same(byte[] a,byte[] b) { return a==null ? b==null : b!=null && a.SequenceEqual(b); }
        string UserConfig(Probe baseline) {
            string home=userHome;
            if (String.IsNullOrEmpty(home) || !Path.IsPathRooted(home) || !Path.IsPathRooted(baseline.Home) || !Path.IsPathRooted(baseline.Config)) return null;
            string local=Path.GetFullPath(Path.Combine(home,"config.toml"));
            if (!String.Equals(local,Path.GetFullPath(Path.Combine(baseline.Home,"config.toml")),StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(local,Path.GetFullPath(baseline.Config),StringComparison.OrdinalIgnoreCase)) return null;
            return local;
        }
        internal int Run(bool checkOnly, Func<bool> authorize, int port) {
            string version=call("--version",0).Trim();
            say("桌面后端："+(Regex.IsMatch(version,@"^codex-cli [a-zA-Z0-9.+-]+$") ? version : "已定位"));
            string help=call("doctor --help",0);
            if (!help.Contains("--json")) { say("此版本没有可识别的 doctor JSON 检查；让本机 AI 按传输流程查询故障时段日志。"); return 2; }
            string args="doctor --json"+(help.Contains("--network-only") ? " --network-only" : "");
            var baseline=Doctor("当前默认配置",args,0);
            if (baseline.Good) { say("服务 HTTP 和 WebSocket 握手均已通过。若实际聊天出现过 5/5，请按发生时间查建连后断流和 HTTP 回退，并做桌面首轮及空闲后验收。"); return 0; }
            if (baseline.WebSocketGood) return Partial();
            if (baseline.ClosedEarly) { say("握手完成后连接已关闭；转入传输流程，关联真实聊天的关闭码、完成事件及代理端同一时刻的记录。"); return 2; }
            string effective=Effective();
            say("当前有效开关："+effective);
            if (!baseline.TransportFailure || effective=="unknown") { say("请按实际错误检查登录、提供方或版本，读取同一聊天的传输事件。"); return 2; }
            // The current, known flag value serves as the first arm of the A/B comparison.
            bool wasOff=effective=="false";
            var other=Doctor(wasOff ? "临时启用系统代理" : "临时关闭系统代理", args+(wasOff ? " --enable " : " --disable ")+Flag,0);
            if (other.WebSocketGood && !other.HttpGood) return Partial();
            if (wasOff && other.Good) {
                if (baseline.ProxySetting=="enabled" || other.ProxySetting=="disabled" || !call("features enable --help",0).Contains("features enable")) {
                    say("开关报告不一致或当前版本缺少保存命令；由本机 AI 检查配置覆盖和用户级 TOML。"); return 2;
                }
                string file=UserConfig(baseline);
                if (file==null) {say("无法用 doctor 核实用户配置路径；由本机 AI 核实有效目录，再保存这一项。"); return 2;}
                if (checkOnly) { say("开关对照命中；已完成只读检查。经本人授权启动修复即可保存。"); return 3; }
                say("将备份当前用户 Codex 配置目录的 config.toml，并仅启用 "+Flag+"。");
                if (!authorize()) { say("保留当前配置。"); return 3; }
                return Save(file,args);
            }
            if (!wasOff && other.Good) { say("仅临时关闭开关时成功。请对照 Windows 当前选路和运行时覆盖，按本机证据修正后复测。"); return 2; }
            bool listener=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep=>ep.Port==port &&
                (IPAddress.IsLoopback(ep.Address)||ep.Address.Equals(IPAddress.Any)||ep.Address.Equals(IPAddress.IPv6Any)));
            if (!listener) { say("本机未监听指定端口。确认客户端正在运行；端口不同时用 --port 实际端口。"); return 2; }
            var directProxy=Doctor("仅此子进程显式使用本机代理",args+" --disable "+Flag,port);
            if (directProxy.WebSocketGood && !directProxy.HttpGood) return Partial();
            say(directProxy.Good ? "进入 Windows 系统选路分支：运行同目录 Check-WindowsProxy.exe；修复实际取值后重新运行本工具。"
                : "显式代理也未恢复握手：按节点路径分支对照可用节点、真实核心报错及故障时段记录，再复测 Codex。");
            return directProxy.Good ? 4 : 2;
        }
        int Save(string file,string args) {
            if(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                throw new RepairFailure("配置是重解析点，请由本机 AI 核对实际文件后定向修改。");
            byte[] before=HashFile(file);
            string backup=file+".pre-reconnect-"+Guid.NewGuid().ToString("N")+".bak";
            if(before!=null) {
                File.Copy(file,backup,false);
                if(!Same(before,HashFile(backup))) throw new RepairFailure("备份校验失败");
                say("本机配置目录内的备份："+Path.GetFileName(backup));
            }
            if (!Same(before,HashFile(file)) || Effective()!="false") throw new RepairFailure("配置已变化，请重新检查");
            byte[] after=null;
            try {
                call("features enable "+Flag,0);
                after=HashFile(file);
                if (after==null || Effective()!="true") throw new RepairFailure("保存后的有效开关未生效");
                var verified=Doctor("保存后无临时覆盖",args,0);
                if (!verified.Good) {
                    Rollback(file,backup,before,after);
                    if (verified.WebSocketGood) return Partial();
                    say("保存后网络验证未通过。请复核服务状态与实际选路。");
                    return 2;
                }
                say("服务 HTTP 和 WebSocket 验证均通过。等进行中的任务结束，从普通图标重开 Codex，在原工作空间新建聊天并接收完整回复，空闲后再测一次。");
                return 0;
            } catch {
                Rollback(file,backup,before,after);
                throw;
            }
        }

        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);

        static bool OnlyFlagChanged(byte[] original, byte[] updated) {
            var utf8=new UTF8Encoding(false,true);
            string previous=utf8.GetString(original);
            string next=utf8.GetString(updated);
            foreach (Match match in Regex.Matches(next,@"(?m)^[ \t]*respect_system_proxy[ \t]*=[ \t]*(?<v>true)[ \t]*(?:#[^\r\n]*)?(?:\r?\n|\z)")) {
                Group value=match.Groups["v"];
                if (next.Remove(value.Index,value.Length).Insert(value.Index,"false")==previous ||
                    (Regex.IsMatch(match.Value,@"\Arespect_system_proxy = true(?:\r?\n)?\z") &&
                     next.Remove(match.Index,match.Length)==previous)) return true;
            }
            return next.StartsWith(previous,StringComparison.Ordinal) &&
                Regex.IsMatch(next.Substring(previous.Length),@"\A(?:\r?\n){0,2}\[features\]\r?\nrespect_system_proxy = true(?:\r?\n)?\z");
        }

        // Validate and restore on one exclusive Windows handle. A racing save either
        // wins before the open (and fails our comparison) or cannot replace the open file.
        internal static bool TryExactUndo(string file,string backup,byte[] before,byte[] after) {
            if (after==null) return false;
            byte[] original=before==null ? new byte[0] : File.ReadAllBytes(backup);
            using (var sha=SHA256.Create()) {
                if (before!=null && !Same(before,sha.ComputeHash(original))) return false;
                var rights=FileSystemRights.Read | (before==null ? FileSystemRights.Delete : FileSystemRights.Write);
                using (var stream=new FileStream(file,FileMode.Open,rights,FileShare.None,4096,FileOptions.None)) {
                    if ((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 || !Same(after,sha.ComputeHash(stream))) return false;
                    stream.Position=0;
                    byte[] updated;
                    using (var buffer=new MemoryStream()) { stream.CopyTo(buffer); updated=buffer.ToArray(); }
                    if (!OnlyFlagChanged(original,updated)) return false;
                    if (before==null) {
                        byte deleteOnClose=1;
                        // FILE_DISPOSITION_INFO applies to this verified file until the handle closes.
                        return SetFileInformationByHandle(stream.SafeFileHandle,4,ref deleteOnClose,1);
                    }
                    stream.Position=0;
                    stream.Write(original,0,original.Length);
                    stream.SetLength(original.Length);
                    stream.Flush(true);
                    return true;
                }
            }
        }
        void Rollback(string file,string backup,byte[] before,byte[] after) {
            try {
                if (TryExactUndo(file,backup,before,after)) {
                    say("已撤回本次单项改动并恢复原配置状态。");
                    return;
                }
            } catch (IOException) {
                // Sharing violations and missing files must leave the active path alone.
            } catch (UnauthorizedAccessException) {
            } catch (DecoderFallbackException) {
            }
            say("未自动回滚：无法独占核实本次单项差异。请保留当前配置，按原值只核对 [features] 中的 respect_system_proxy。");
        }
    }
    internal static class Program {
        static readonly string[] ProxyVariables={"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY"};
        static string Persistent(string name) {
            return Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.User) ??
                   Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Machine);
        }
        static string NormalHome() {
            string defaultHome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            string expected=Persistent("CODEX_HOME");
            if(String.IsNullOrEmpty(expected)) expected=defaultHome;
            string actual=Environment.GetEnvironmentVariable("CODEX_HOME");
            if(String.IsNullOrEmpty(actual)) actual=defaultHome;
            if(!Path.IsPathRooted(expected) || !Path.IsPathRooted(actual) ||
                !String.Equals(Path.GetFullPath(expected).TrimEnd('\\'),Path.GetFullPath(actual).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("当前进程配置目录与普通用户环境不同，请从资源管理器运行以检查真实桌面配置。");
                return null;
            }
            return expected;
        }
        internal static string SelectBackend(string registeredLocation,IEnumerable<string> running) {
            if (String.IsNullOrWhiteSpace(registeredLocation) || !Path.IsPathRooted(registeredLocation))
                throw new RepairFailure("未能确认官方商店安装包。请核对目标桌面应用。");
            string trusted=Path.GetFullPath(Path.Combine(registeredLocation,@"app\resources\codex.exe"));
            var candidates=new HashSet<string>(running,StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(trusted) || (candidates.Count>0 && !candidates.Contains(trusted)))
                throw new RepairFailure("已安装的官方后端与当前运行位置不符，或此布局暂未支持。请打开目标商店版再试。");
            return trusted;
        }
        static string FindBackend() {
            string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            string lookup="-NoLogo -NoProfile -NonInteractive -Command \""
                +"$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                +"Import-Module (Join-Path $PSHOME 'Modules\\Appx\\Appx.psd1'); "
                +"$packages=@(Appx\\Get-AppxPackage -Name OpenAI.Codex | Where-Object { "
                +"$_.PackageFamilyName -ceq 'OpenAI.Codex_2p2nqsd0c76g0' -and $_.SignatureKind -eq 'Store' "
                +"-and -not $_.IsDevelopmentMode -and $_.Status -eq 'Ok' }); "
                +"if ($packages.Count -eq 1) { $packages[0].InstallLocation }\"";
            string location=Exec(shell,lookup,0).Trim();
            var running=new List<string>();
            foreach (var process in Process.GetProcessesByName("codex")) {
                using (process) {
                    try {
                        string path=process.MainModule.FileName;
                        if (Regex.IsMatch(path,@"\\resources\\(?:[^\\]+\\)*codex\.exe$",RegexOptions.IgnoreCase)) running.Add(path);
                    } catch (System.ComponentModel.Win32Exception) {} catch (InvalidOperationException) {}
                }
            }
            return SelectBackend(location,running);
        }
        static string Exec(string exe,string args,int port) {
            var info=new ProcessStartInfo(exe,args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
                RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
                WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            foreach(string name in ProxyVariables) {
                string value=Persistent(name);
                if(String.IsNullOrEmpty(value)) info.EnvironmentVariables.Remove(name);
                else info.EnvironmentVariables[name]=value;
            }
            if (port>0) {
                foreach(string name in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"}) info.EnvironmentVariables[name]="http://127.0.0.1:"+port;
                info.EnvironmentVariables["NO_PROXY"]="localhost,127.0.0.1,::1";
            }
            using(var p=Process.Start(info)) {
                var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
                // A config editor must finish its own transaction. Only terminate this tool's timed-out diagnostic.
                if(args.StartsWith("doctor ",StringComparison.Ordinal) && !p.WaitForExit(120000)) {
                    try {p.Kill();} catch(InvalidOperationException) {} throw new RepairFailure("doctor 在 120 秒内未完成");
                }
                p.WaitForExit(); string raw=stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
                if (p.ExitCode!=0 && !args.StartsWith("doctor --json",StringComparison.Ordinal)) throw new RepairFailure("子进程未成功完成，请核对安装包、后端版本和对应诊断步骤。");
                return raw;
            }
        }
        internal static string FailureMessage(string stage,Exception error) {
            var known=error as RepairFailure;
            return "未完成（"+stage+"）："+(known!=null ? known.Message :
                String.Format("{0}，错误码 0x{1:X8}",error.GetType().Name,error.HResult));
        }
        static int Main(string[] args) {
            bool check=false, apply=false, pause=Environment.UserInteractive && !Console.IsInputRedirected;
            int port=10808;
            for(int i=0;i<args.Length;i++) {
                if(args[i]=="--check") check=true;
                else if(args[i]=="--apply") apply=true;
                else if(args[i]=="--no-pause") pause=false;
                else if(args[i]=="--port" && i+1<args.Length && Int32.TryParse(args[++i],out port) && port>0 && port<=65535) {}
                else { Console.WriteLine("Repair-CodexReconnect [--check | --apply] [--port PORT] [--no-pause]"); return args.Contains("--help")?0:1; }
            }
            if(check && apply) return 1;
            int rc=1;
            string stage="校验用户环境";
            try {
                string home=NormalHome();
                if(home==null) throw new RepairFailure("配置目录校验未通过，保留现有设置。");
                var overrides=ProxyVariables.Where(n=>Environment.GetEnvironmentVariable(n)!=Persistent(n)).ToArray();
                if(overrides.Length>0) Console.WriteLine("诊断子进程已还原普通用户的代理变量来源，仅列出变量名："+String.Join(",",overrides));
                stage="验证官方桌面后端";
                string exe=FindBackend();
                using(var mutex=new Mutex(false,@"Local\CodexNetworkReconnectRepair")) {
                    bool owns=false;
                    try {
                        try {owns=mutex.WaitOne(0);} catch(AbandonedMutexException){owns=true;}
                        if(!owns) throw new RepairFailure("本登录会话已有修复检查在运行。");
                        var core=new Core((cmd,p)=>Exec(exe,cmd,p),Console.WriteLine,home);
                        stage="网络检查与配置验证";
                        rc=core.Run(check,()=>{
                            if(apply) return true;
                            if(Console.IsInputRedirected) return false;
                            Console.Write("同意保存并复测？[y/N] ");
                            return String.Equals(Console.ReadLine(),"y",StringComparison.OrdinalIgnoreCase);
                        },port);
                        if(rc==4) {
                            var checker=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Check-WindowsProxy.exe");
                            if(!File.Exists(checker)) throw new RepairFailure("已定位显式代理分支；缺少 Windows 检查器，请用 references/windows.md 的原生对照。");
                            try { Console.WriteLine(Exec(checker,"--no-pause --port "+port,0).TrimEnd()); }
                            catch { throw new RepairFailure("已定位显式代理分支；Windows 检查未完成，请按 references/windows.md 采集。"); }
                        }
                    } finally {if(owns) mutex.ReleaseMutex();}
                }
            } catch(Exception e) {
                rc=1;
                Console.WriteLine(FailureMessage(stage,e));
            }
            if(pause){Console.WriteLine("按回车关闭。");Console.ReadLine();}
            return rc;
        }
    }
}
