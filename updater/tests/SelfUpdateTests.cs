using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VirgilPatch;

class SelfUpdateTests
{
    static int passed;
    static RSACryptoServiceProvider key;
    static JavaScriptSerializer js = new JavaScriptSerializer();
    static string payload;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        catch (ArgumentException) { rejected = true; }
        catch (KeyNotFoundException) { rejected = true; }
        Assert(rejected, "Unsafe release was accepted.");
    }
    static Dictionary<string,object> Release(string version = "1.4.1.0")
    {
        var size = new FileInfo(payload).Length;
        var hash = ProgramRelease.FileHash(payload);
        var signature = key.SignData(ProgramRelease.SigningBytes(version,size,hash),CryptoConfig.MapNameToOID("SHA256"));
        return new Dictionary<string,object> { {"format",1},{"version",version},{"file",ProgramRelease.BinaryPath},
            {"size",size},{"sha256",hash},{"signature",Convert.ToBase64String(signature)} };
    }
    static byte[] Bytes(Dictionary<string,object> d) { return Encoding.UTF8.GetBytes(js.Serialize(d)); }
    static ProgramRelease Parse(Dictionary<string,object> d) { return ProgramRelease.Parse(Bytes(d),key.ToXmlString(false)); }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static int Main(string[] args)
    {
        try
        {
            payload = args[0];
            key = new RSACryptoServiceProvider(new CspParameters(24)); key.PersistKeyInCsp=false;
            key.FromXmlString(File.ReadAllText(args[1]));
            Test("valid-signature-and-binary", () => Parse(Release()).ValidateBinary(payload));
            Test("wrong-signing-key-rejected", delegate {
                using (var other = new RSACryptoServiceProvider(new CspParameters(24)))
                { other.PersistKeyInCsp=false; Reject(() => ProgramRelease.Parse(Bytes(Release()),other.ToXmlString(false))); }
            });
            Test("altered-signed-fields-rejected", delegate {
                foreach (var pair in new Dictionary<string,object> { {"version","1.4.2.0"},{"size",4096},{"sha256",new string('a',64)} })
                { var d=Release(); d[pair.Key]=pair.Value; Reject(() => Parse(d)); }
            });
            Test("executable-path-scope-rejected", delegate {
                foreach (var path in new[] {"evil.exe","../evil.exe","https://evil.invalid/app.exe","updater/other.exe"})
                { var d=Release(); d["file"]=path; Reject(() => Parse(d)); }
            });
            Test("invalid-release-versions-rejected", delegate {
                foreach (var version in new[] {"1.4.1","01.4.1.0","../1.4.1.0","1.4.1.0\n"})
                { var d=Release(); d["version"]=version; Reject(() => Parse(d)); }
            });
            Test("oversized-binary-rejected", delegate {var d=Release(); d["size"]=ProgramRelease.MaxBinaryBytes+1; Reject(() => Parse(d));});
            Test("invalid-signature-encoding-rejected", delegate {var d=Release(); d["signature"]="not-base64"; Reject(() => Parse(d));});
            Test("missing-and-extra-fields-rejected", delegate {
                var d=Release(); d.Remove("signature"); Reject(() => Parse(d));
                d=Release(); d.Add("url","https://evil.invalid"); Reject(() => Parse(d));
            });
            Test("oversized-metadata-rejected", () => Reject(() => ProgramRelease.Parse(new byte[65537],key.ToXmlString(false))));
            Test("binary-version-mismatch-rejected", () => Reject(() => Parse(Release("1.4.2.0")).ValidateBinary(payload)));
            Test("binary-checksum-mismatch-rejected", delegate {
                var copy=Path.Combine(Path.GetDirectoryName(args[1]),"tampered.exe");
                var bytes=File.ReadAllBytes(payload); bytes[bytes.Length-1]^=1; File.WriteAllBytes(copy,bytes);
                Reject(() => Parse(Release()).ValidateBinary(copy));
            });
            Test("windows-argument-quoting-roundtrip", delegate {
                var values=new[] {"", @"C:\folder with spaces\", "x\"y", "x\\\"y", "$()`&'|", "unicode-é"};
                var command="program.exe "+string.Join(" ",values.Select(SelfUpdate.QuoteArgument));
                int count; var ptr=CommandLineToArgvW(command,out count);
                Assert(ptr!=IntPtr.Zero && count==values.Length+1,"Wrong argument count.");
                try { for(int i=0;i<values.Length;i++) Assert(Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr,(i+1)*IntPtr.Size))==values[i],"Argument changed."); }
                finally {LocalFree(ptr);}
            });
            Test("internal-handoff-arguments-not-forwarded", delegate {
                var clean=SelfUpdate.UserArguments(new[] {"--state","path with spaces","--confirm-self-update","old-job","--skip-self-update-once"});
                Assert(clean.SequenceEqual(new[] {"--state","path with spaces"}),"Internal control arguments accumulated.");
            });
            Console.WriteLine("TOTAL "+passed+" self-update validation tests passed"); return 0;
        }
        catch(Exception ex) {Console.WriteLine("FAIL "+ex);return 1;}
        finally {if(key!=null) key.Dispose();}
    }
}
