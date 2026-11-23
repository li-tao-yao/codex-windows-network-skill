using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Windows .NET Framework 4.x. The installed desktop backend handles authentication and TOML edits.
namespace CodexReconnect {
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
            object v; return row.TryGetValue(key, out v) && v is string ? (string)v : "";
        }
        Dictionary<string,object> ObjectAt(Dictionary<string,object> row, string key) {
            object v; return row.TryGetValue(key, out v) && v is Dictionary<string,object> ? (Dictionary<string,object>)v : empty;
        }
        Dictionary<string,object> Row(Dictionary<string,object> report, string id) {
            var map = ObjectAt(report, "checks");
            if (map.Count != 0) return ObjectAt(map,id);
            object rows;
            if (report.TryGetValue("checks",out rows) && rows is object[])
                foreach (var item in ((object[])rows).OfType<Dictionary<string,object>>())
                    if (Field(item,"id")==id) return item;
            return empty;
        }
        sealed class Probe {
            public bool Good, TransportFailure, ClosedEarly;
            public string Config, Home, ProxySetting;
        }
        Probe Doctor(string label, string args, int port) {
            var json=call(args,port);
            Dictionary<string,object> report;
            try { report=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json); }
            catch(ArgumentException) { throw new InvalidDataException("此版本的 doctor 未返回可识别的 JSON。"); }
            catch(InvalidOperationException) { throw new InvalidDataException("此版本的 doctor 报告格式无法读取。"); }
            if (report == null) throw new InvalidDataException("doctor 返回空报告");
            var ws = Row(report,"network.websocket_reachability");
            var http = Row(report,"network.provider_reachability");
            var details = ObjectAt(ws,"details");
            string status = Field(ws,"status"), summary = Field(ws,"summary"), handshake = Field(details,"handshake result");
            bool closedEarly=Regex.IsMatch(summary,@"closed (?:immediately )?after (?:the )?handshake|closed .*before response\.completed",RegexOptions.IgnoreCase)
                || (handshake.IndexOf("101",StringComparison.Ordinal)>=0 && Regex.IsMatch(Field(details,"immediate close code"),@"\b[1-4]\d{3}\b"));
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
                label,Field(http,"status"),status,closedEarly ? "已升级后断开" : good ? "成功" : failed ? "传输失败" : "查错误原文",
                ws.TryGetValue("durationMs",out duration) ? Convert.ToString(duration) : "?"));
            var cfg=ObjectAt(Row(report,"config.load"),"details");
            return new Probe {Good=good, TransportFailure=failed, ClosedEarly=closedEarly, Config=Field(cfg,"config.toml"),
                Home=Field(cfg,"CODEX_HOME"), ProxySetting=Field(ObjectAt(Row(report,"network.env"),"details"),"respect system proxy")};
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
            if (baseline.Good) { say("已通过握手检查。若实际聊天出现过 5/5，请按发生时间查建连后断流和 HTTP 回退，并做桌面首轮及空闲后验收。"); return 0; }
            if (baseline.ClosedEarly) { say("握手完成后连接已关闭；转入传输流程，关联真实聊天的关闭码、完成事件及代理端同一时刻的记录。"); return 2; }
            string effective=Effective();
            say("当前有效开关："+effective);
            if (!baseline.TransportFailure || effective=="unknown") { say("请按实际错误检查登录、提供方或版本，读取同一聊天的传输事件。"); return 2; }
            // The current, known flag value serves as the first arm of the A/B comparison.
            bool wasOff=effective=="false";
            var other=Doctor(wasOff ? "临时启用系统代理" : "临时关闭系统代理", args+(wasOff ? " --enable " : " --disable ")+Flag,0);
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
            say(directProxy.Good ? "进入 Windows 系统选路分支：运行同目录 Check-WindowsProxy.exe；修复实际取值后重新运行本工具。"
                : "显式代理也未恢复握手：按节点路径分支对照可用节点、真实核心报错及故障时段记录，再复测 Codex。");
            return directProxy.Good ? 4 : 2;
        }
        int Save(string file,string args) {
            if(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("配置是重解析点，请由本机 AI 核对实际文件后定向修改。");
            byte[] before=HashFile(file);
            string backup=file+".pre-reconnect-"+Guid.NewGuid().ToString("N")+".bak";
            if(before!=null) {
                File.Copy(file,backup,false);
                if(!Same(before,HashFile(backup))) throw new IOException("备份校验失败");
                say("本机配置目录内的备份："+Path.GetFileName(backup));
            }
            if (!Same(before,HashFile(file)) || Effective()!="false") throw new IOException("配置已变化，请重新检查");
            byte[] after=null;
            try {
                call("features enable "+Flag,0);
                after=HashFile(file);
                if (after==null || Effective()!="true") throw new IOException("保存后的有效开关未生效");
                if (!Doctor("保存后无临时覆盖",args,0).Good) throw new IOException("保存后握手未通过");
                say("配置与握手验证通过。等进行中的任务结束，从普通图标重开 Codex，新建聊天并接收完整回复；空闲后再测一次。");
                return 0;
            } catch {
                say("持久化或复测未完成。请保留本机备份。");
                // Some native failures occur after the flag has reached disk.
                if(after==null) after=HashFile(file);
                if (after!=null && Same(after,HashFile(file)) && !Same(before,after)) {
                    if (TryExactUndo(file,backup,before,after)) say("已撤回本次配置改动，恢复写入前的文件状态。");
                    else if(Effective()=="true" && Same(after,HashFile(file))) {
                        call("features disable "+Flag,0);
                        if(Effective()=="false") say("已通过原生编辑器恢复该开关原有效值 false。原先未显式设置时，文件中可能留有 false 项。");
                        else say("原生回滚未验证，请根据本机备份手工核对该开关。");
                    }
                } else if(after!=null && !Same(after,HashFile(file))) {
                    say("检测到并发编辑，已保留现有文件。本次开关可能仍为 true；请按备份仅核对和恢复这一项。");
                }
                throw;
            }
        }
        bool TryExactUndo(string file,string backup,byte[] before,byte[] after) {
            if(before!=null && !Same(before,HashFile(backup))) return false;
            string original=before==null ? "" : File.ReadAllText(backup);
            string updated=File.ReadAllText(file);
            bool simple=false;
            foreach(Match m in Regex.Matches(updated,@"(?m)^[ \t]*respect_system_proxy[ \t]*=[ \t]*(?<v>true)[ \t]*(?:#[^\r\n]*)?(?:\r?\n|\z)")) {
                Group v=m.Groups["v"];
                string flipped=updated.Remove(v.Index,v.Length).Insert(v.Index,"false");
                string removed=updated.Remove(m.Index,m.Length);
                if(flipped==original || removed==original) {simple=true;break;}
                // Match a newly appended [features] table and the one key, including for a new config.
                if(updated.StartsWith(original,StringComparison.Ordinal) &&
                    Regex.IsMatch(updated.Substring(original.Length),@"\A\s*\[features\][ \t]*\r?\n[ \t]*respect_system_proxy[ \t]*=[ \t]*true\s*\z")) {simple=true;break;}
            }
            if(!simple || !Same(after,HashFile(file))) return false;
            string nonce=Guid.NewGuid().ToString("N"), displaced=file+".undo-"+nonce;
            if(before==null) File.Move(file,displaced);
            else {
                string staging=file+".restore-"+nonce;
                File.Copy(backup,staging,false);
                try { File.Replace(staging,file,displaced); }
                finally { if(File.Exists(staging)) File.Delete(staging); }
            }
            if(!Same(after,HashFile(displaced))) {
                say("本机配置目录保留了回滚期间的并发编辑："+Path.GetFileName(displaced)+"。请本机 AI 合并。");
                throw new IOException("检测到并发修改");
            }
            File.Delete(displaced);
            return true;
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
        static string FindBackend() {
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in Process.GetProcessesByName("codex")) {
                using(p) {
                    try {
                        string path=p.MainModule.FileName;
                        if(Regex.IsMatch(path,@"\\resources\\(?:[^\\]+\\)*codex\.exe$",RegexOptions.IgnoreCase)) paths.Add(path);
                    } catch(System.ComponentModel.Win32Exception) {} catch(InvalidOperationException) {}
                }
            }
            if(paths.Count!=1) {
                string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
                string location=Exec(shell,"-NoLogo -NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1).InstallLocation\"",0).Trim();
                if(!String.IsNullOrWhiteSpace(location) && Path.IsPathRooted(location)) {
                    string candidate=Path.Combine(location,@"app\resources\codex.exe");
                    if(File.Exists(candidate) && (paths.Count==0 || paths.Contains(candidate))) return candidate;
                }
                throw new InvalidOperationException("请打开并登录目标 Codex 桌面应用，核对正在运行的后端版本。");
            }
            return paths.Single();
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
                    try {p.Kill();} catch(InvalidOperationException) {} throw new TimeoutException("doctor 在 120 秒内未完成");
                }
                p.WaitForExit(); string raw=stdout.GetAwaiter().GetResult(); stderr.GetAwaiter().GetResult();
                if (p.ExitCode!=0 && !args.StartsWith("doctor --json",StringComparison.Ordinal)) throw new InvalidOperationException("桌面后端未完成 "+args);
                return raw;
            }
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
            try {
                string home=NormalHome();
                if(home==null) throw new InvalidOperationException("配置目录校验未通过，保留现有设置。");
                var overrides=ProxyVariables.Where(n=>Environment.GetEnvironmentVariable(n)!=Persistent(n)).ToArray();
                if(overrides.Length>0) Console.WriteLine("诊断子进程已还原普通用户的代理变量来源，仅列出变量名："+String.Join(",",overrides));
                string exe=FindBackend();
                using(var mutex=new Mutex(false,@"Local\CodexNetworkReconnectRepair")) {
                    bool owns=false;
                    try {
                        try {owns=mutex.WaitOne(0);} catch(AbandonedMutexException){owns=true;}
                        if(!owns) throw new InvalidOperationException("本登录会话已有修复检查在运行。");
                        var core=new Core((cmd,p)=>Exec(exe,cmd,p),Console.WriteLine,home);
                        rc=core.Run(check,()=>{
                            if(apply) return true;
                            if(Console.IsInputRedirected) return false;
                            Console.Write("同意保存并复测？[y/N] ");
                            return String.Equals(Console.ReadLine(),"y",StringComparison.OrdinalIgnoreCase);
                        },port);
                        if(rc==4) {
                            var checker=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Check-WindowsProxy.exe");
                            if(!File.Exists(checker)) throw new IOException("已定位显式代理分支；缺少 Windows 检查器，请用 references/windows.md 的原生对照。");
                            try { Console.WriteLine(Exec(checker,"--no-pause --port "+port,0).TrimEnd()); }
                            catch { throw new IOException("已定位显式代理分支；Windows 检查未完成，请按 references/windows.md 采集。"); }
                        }
                    } finally {if(owns) mutex.ReleaseMutex();}
                }
            } catch(Exception e) {
                rc=1;
                Console.WriteLine("未完成："+(e is InvalidOperationException || e is TimeoutException || e is IOException ? e.Message : e.GetType().Name));
            }
            if(pause){Console.WriteLine("按回车关闭。");Console.ReadLine();}
            return rc;
        }
    }
}
