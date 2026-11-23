using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using CodexReconnect;

internal static class Regression {
    static int passed;
    static string directory;
    static void Assert(bool condition,string message) {
        if (!condition) throw new Exception(message);
    }
    static void Case(string label,Action check) {
        check();
        Console.WriteLine("PASS "+label);
        passed++;
    }

    sealed class Network {
        internal string Http="ok", Ws="ok";
        internal int? ImmediateClose;
        internal object Report(string home,bool list,bool enabled) {
            var http=new { id="network.provider_reachability", status=Http };
            var ws=new { id="network.websocket_reachability", status=Ws,
                details=new Dictionary<string,object>{{"immediate close code",ImmediateClose}},
                summary=Ws=="ok" ? "Responses WebSocket handshake succeeded" : "Responses WebSocket timed out" };
            var cfg=new { id="config.load", details=new Dictionary<string,string> {
                {"config.toml",Path.Combine(home,"config.toml")},{"CODEX_HOME",home} } };
            var env=new { id="network.env", details=new Dictionary<string,string> {
                {"respect system proxy",enabled ? "enabled" : "disabled"} } };
            if (list) return new object[]{http,ws,cfg,env};
            return new Dictionary<string,object> {
                {http.id,http},{ws.id,ws},{cfg.id,cfg},{env.id,env}
            };
        }
    }
    sealed class Fixture {
        internal readonly string Home=Directory.CreateDirectory(Path.Combine(directory,Guid.NewGuid().ToString("N"))).FullName;
        internal string FileName { get { return Path.Combine(Home,"config.toml"); } }
        internal Network Baseline=new Network{Ws="warning"}, Trial=new Network(), Saved=new Network();
        internal bool ArrayShape, CheckOnly, Authorized, Enabled, Empty, EditedDuringSave;
        internal Action DuringVerification;
        internal int Writes;
        internal readonly List<string> Messages=new List<string>();
        string Invoke(string args,int port) {
            if (args=="--version") return "codex-cli test";
            if (args=="doctor --help") return "--network-only --json";
            if (args=="features list") return "respect_system_proxy experimental "+(Enabled?"true":"false");
            if (args=="features enable --help") return "features enable";
            if (args=="features enable respect_system_proxy") {
                Assert(Authorized && !CheckOnly,"unauthorized configuration change");
                File.AppendAllText(FileName,"\n[features]\nrespect_system_proxy = true\n");
                if (EditedDuringSave) File.AppendAllText(FileName,"# another editor saved this\n");
                Writes++; Enabled=true;
                return "";
            }
            Assert(args.StartsWith("doctor --json"),"unexpected backend command");
            if (Empty) return "{\"checks\":[]}";
            bool trial=args.Contains(" --enable ");
            if (Enabled && DuringVerification!=null) DuringVerification();
            var net=Enabled ? Saved : (trial ? Trial : Baseline);
            return new JavaScriptSerializer().Serialize(new {checks=net.Report(Home,ArrayShape,Enabled||trial)});
        }
        internal int Run() {
            return new Core(Invoke,Messages.Add,Home).Run(CheckOnly,()=>Authorized,10808);
        }
    }
    static void NetworkResults() {
        foreach (bool list in new[]{false,true}) {
            Case("healthy report / "+(list?"array":"object"),()=>{
                var f=new Fixture{ArrayShape=list,Baseline=new Network()};
                Assert(f.Run()==0 && f.Writes==0,"both probes should pass");
            });
        }
        Case("HTTP failed, WebSocket passed",()=>{
            var f=new Fixture{Baseline=new Network{Http="error"}};
            Assert(f.Run()==5 && f.Writes==0 && f.Messages.Any(s=>s.Contains("部分通过")),"partial baseline");
        });
        Case("successful handshake immediately closes with a numeric code",()=>{
            var f=new Fixture{Baseline=new Network{ImmediateClose=1006}};
            Assert(f.Run()==2 && f.Writes==0,"early close treated as healthy");
        });
        Case("missing HTTP result",()=>{
            var f=new Fixture{Baseline=new Network{Http=""}};
            Assert(f.Run()==5 && f.Writes==0,"missing HTTP must not pass");
        });
        Case("temporary feature only partially helps",()=>{
            var f=new Fixture{Authorized=true,Trial=new Network{Http="warning"}};
            Assert(f.Run()==5 && f.Writes==0,"incomplete trial must not save");
        });
        Case("empty report",()=>{
            var f=new Fixture{Empty=true};
            Assert(f.Run()==2 && f.Writes==0,"empty report must not pass");
        });
        foreach (bool check in new[]{true,false}) {
            Case(check?"read-only comparison":"declined save",()=>{
                var f=new Fixture{ArrayShape=true,CheckOnly=check,Authorized=check};
                Assert(f.Run()==3 && f.Writes==0,"configuration must remain untouched");
            });
        }
        Case("authorized save",()=>{
            var f=new Fixture{Authorized=true,ArrayShape=true};
            Assert(f.Run()==0 && f.Writes==1 && File.ReadAllText(f.FileName).Contains("= true"),"authorized flag");
        });
    }

    static void Rollbacks() {
        Case("restore existing file byte for byte",()=>{
            var f=new Fixture{Authorized=true,Saved=new Network{Ws="warning"}};
            File.WriteAllText(f.FileName,"# 保留用户设置\r\n",new UTF8Encoding(true));
            var original=File.ReadAllBytes(f.FileName);
            Assert(f.Run()==2 && File.ReadAllBytes(f.FileName).SequenceEqual(original),"original bytes lost");
        });
        Case("restore absent file on partial post-save probe",()=>{
            var f=new Fixture{Authorized=true,Saved=new Network{Http="error"}};
            Assert(f.Run()==5 && !File.Exists(f.FileName),"new file was not rolled back");
        });
        Case("preserve concurrent change before first read-back",()=>{
            var f=new Fixture{Authorized=true,EditedDuringSave=true,Saved=new Network{Ws="warning"}};
            Assert(f.Run()==2 && File.ReadAllText(f.FileName).Contains("another editor"),"concurrent change lost");
        });
        foreach(bool existed in new[]{true,false}){
            Case("preserve concurrent save at active path / "+(existed?"existing":"new"),()=>{
                var f=new Fixture{Authorized=true,Saved=new Network{Ws="warning"}};
                if(existed) File.WriteAllText(f.FileName,"# preceding settings\n");
                f.DuringVerification=()=>File.WriteAllText(f.FileName,"# saved by the user during verification");
                Assert(f.Run()==2 && File.ReadAllText(f.FileName)=="# saved by the user during verification","active file replaced");
            });
        }
        Case("concurrent comment on newly added flag stays active",()=>{
            string path=Path.Combine(directory,"comment.toml"), backup=path+".bak";
            File.WriteAllText(backup,"[features]\n");
            string current="[features]\nrespect_system_proxy = true # user's new explanation\n";
            File.WriteAllText(path,current);
            Assert(!Core.TryExactUndo(path,backup,Core.HashFile(backup),Core.HashFile(path)) &&
                File.ReadAllText(path)==current,"new comment removed");
        });
        Case("busy file remains active",()=>{
            var f=new Fixture{Authorized=true,Saved=new Network{Ws="warning"}};
            FileStream other=null;
            try {
                f.DuringVerification=()=>other=File.Open(f.FileName,FileMode.Open,FileAccess.Read,FileShare.Read);
                Assert(f.Run()==2 && File.Exists(f.FileName),"locked file moved");
                Assert(f.Messages.Any(s=>s.Contains("未自动回滚")),"busy fallback not reported");
            } finally { if (other!=null) other.Dispose(); }
        });
    }
    static void RejectBackend(string location,IEnumerable<string> running) {
        try { CodexReconnect.Program.SelectBackend(location,running); }
        catch (RepairFailure) { return; }
        throw new Exception("unverified backend accepted");
    }
    public static int Main(string[] args) {
        directory=args[0];
        NetworkResults(); Rollbacks();
        Case("registered package is required",()=>RejectBackend("",new string[0]));
        Case("different running backend is rejected and only registered file can be selected",()=>{
            var official=Path.Combine(directory,@"app\resources\codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(official)); File.WriteAllText(official,"fixture");
            string other=Path.Combine(directory,@"other\resources\codex.exe");
            RejectBackend(directory,new[]{other});
            Assert(CodexReconnect.Program.SelectBackend(directory,new[]{official,other})==official,"wrong package");
            Assert(CodexReconnect.Program.SelectBackend(directory,new string[0])==official,"installed fallback");
        });
        Case("filesystem exception text stays private",()=>{
            var error=new IOException("fixture-private-path");
            string shown=CodexReconnect.Program.FailureMessage("测试",error);
            Assert(!shown.Contains("fixture-private") && shown.Contains("0x") && shown.Contains("IOException"),"exception leak");
        });
        Case("dial-up name without reversible alias",()=>{
            Assert(ProxyCheck.Program.CandidateName("中")==null,"expected no alias");
            using (var output=new StringWriter()) {
                var console=Console.Out;
                try {
                    Console.SetOut(output);
                    // Use an existing read-only registry key supplied by Windows; never write to Internet Settings.
                    using (var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                        ProxyCheck.Program.ReportDialupRecords(key,"中",Microsoft.Win32.RegistryView.Default);
                        ProxyCheck.Program.ReportDialupRecords(key,"ProductName",Microsoft.Win32.RegistryView.Default);
                    }
                } finally { Console.SetOut(console); }
                string seen=output.ToString();
                Assert(seen.Contains("correct-name=False") && seen.Contains("correct-name=True") &&
                    seen.Contains("Correct-name record: type is not REG_BINARY"),"exact lookup was skipped");
            }
        });
        Console.WriteLine(passed+" regression cases passed.");
        return 0;
    }
}
