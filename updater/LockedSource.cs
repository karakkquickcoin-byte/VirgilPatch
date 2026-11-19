using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VirgilPatch {
    // Only the game-patch channel is wrapped. Signed program upgrades remain password independent.
    sealed class LockedSource : ISource {
        readonly ISource inner;
        readonly Func<string, bool, string> ask;
        Dictionary<string, object> objects;
        string unlockedCommit;
        byte[] unlockedIndex;
        public LockedSource(ISource source, Func<string,bool,string> prompt) { inner=source; ask=prompt; }
        public string ResolveCommit() { return inner.ResolveCommit(); }
        public string Describe() { return inner.Describe(); }
        internal static byte[] B64(object value) { return Convert.FromBase64String((string)value); }
        internal static bool Equal(byte[] a, byte[] b) {
            if(a.Length!=b.Length) return false;
            int diff=0; for(int i=0;i<a.Length;i++) diff|=a[i]^b[i]; return diff==0;
        }
        internal static byte[] Open(byte[] payload, byte[] key) {
            using(var input=new MemoryStream(payload)) using(var output=new MemoryStream()) {
                Decrypt(input,output,key,Safety.MaxIndexBytes); return output.ToArray();
            }
        }
        internal static void Decrypt(Stream input, Stream output, byte[] key, long maxPlain) {
            if(key.Length!=64 || input.Length<68 || (input.Length-52)%16!=0) throw new InvalidDataException("Invalid encrypted payload.");
            byte[] header=new byte[20]; if(input.Read(header,0,20)!=20 || Encoding.ASCII.GetString(header,0,4)!="VGL1") throw new InvalidDataException("Invalid encrypted header.");
            long cipherBytes=input.Length-52;
            if(cipherBytes>maxPlain+16) throw new InvalidDataException("Encrypted payload exceeds its size limit.");
            byte[] buffer=new byte[1024*1024], tag=new byte[32];
            using(var mac=new HMACSHA256(key.Skip(32).ToArray())) {
                mac.TransformBlock(header,0,header.Length,null,0);
                long left=cipherBytes; while(left>0) {
                    int n=input.Read(buffer,0,(int)Math.Min(left,buffer.Length)); if(n==0)throw new EndOfStreamException();
                    mac.TransformBlock(buffer,0,n,null,0); left-=n;
                }
                mac.TransformFinalBlock(new byte[0],0,0);
                if(input.Read(tag,0,32)!=32 || !Equal(tag,mac.Hash)) throw new CryptographicException("Incorrect password or damaged encrypted payload.");
            }
            // Authenticate the complete ciphertext before any plaintext is written.
            input.Position=20;
            using(var aes=Aes.Create()) {
                aes.Key=key.Take(32).ToArray(); aes.IV=header.Skip(4).ToArray(); aes.Mode=CipherMode.CBC; aes.Padding=PaddingMode.PKCS7;
                using(var decryptor=aes.CreateDecryptor()) using(var crypto=new CryptoStream(output,decryptor,CryptoStreamMode.Write,true)) {
                    long left=cipherBytes;
                    while(left>0) { int n=input.Read(buffer,0,(int)Math.Min(left,buffer.Length)); if(n==0)throw new EndOfStreamException(); crypto.Write(buffer,0,n); left-=n; }
                    crypto.FlushFinalBlock();
                }
            }
            if(output.Length>maxPlain) throw new InvalidDataException("Decrypted payload exceeds its size limit.");
        }
        static byte[] CatalogKey(Dictionary<string,object> outer,string password) {
            foreach(string label in new[]{"weekly","recovery"}) {
                var slot=(Dictionary<string,object>)outer[label]; byte[] salt=B64(slot["salt"]);
                if(salt.Length!=16) throw new InvalidDataException("Invalid password salt.");
                using(var kdf=new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password),salt,600000,HashAlgorithmName.SHA256)) {
                    try { var key=Open(B64(slot["key"]),kdf.GetBytes(64)); if(key.Length!=64)throw new InvalidDataException("Invalid catalog key."); return key; }
                    catch(CryptographicException) { }
                }
            }
            return null;
        }
        // Update-button gate (1.5.13): patch files stay plain on GitHub; index.json carries a salted PBKDF2 check of
        // this week's code. Update asks for it before any download. A spoiler blocker, not file encryption.
        // The unlocked Pass is remembered (its public check value only, never the code) so players are asked once per Pass.
        internal static string GateMemoryPath { get { return Path.Combine(Config.StateDir,"weekly-unlock.txt"); } }
        static bool GateMatches(string password,Dictionary<string,object> slot) {
            if(slot==null) return false;
            byte[] salt=B64(slot["salt"]), check=B64(slot["check"]);
            if(salt.Length!=16 || check.Length!=32) throw new InvalidDataException("Invalid update code check.");
            using(var kdf=new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password),salt,600000,HashAlgorithmName.SHA256))
                return Equal(kdf.GetBytes(32),check);
        }
        void CheckGate(Dictionary<string,object> gate) {
            if(gate==null || Convert.ToInt32(gate["format"])!=1 || Convert.ToInt32(gate["iterations"])!=600000) throw new InvalidDataException("Unsupported update code check. Update the launcher.");
            string number=gate["pass_number"] as string;
            if(number==null || !Regex.IsMatch(number,@"\A0\.[1-9][0-9]{0,8}\z")) throw new InvalidDataException("Invalid password number.");
            var weekly=gate["weekly"] as Dictionary<string,object>;
            object r; var recovery=gate.TryGetValue("recovery",out r) ? r as Dictionary<string,object> : null;
            if(weekly==null) throw new InvalidDataException("Invalid update code check.");
            string memory=number+"\n"+(string)weekly["check"];
            try { var f=new FileInfo(GateMemoryPath); if(f.Exists && f.Length<4096 && File.ReadAllText(f.FullName)==memory) return; } catch(IOException) { } catch(UnauthorizedAccessException) { }
            for(int attempt=0;attempt<3;attempt++) {
                string password=ask(number,attempt>0);
                if(password==null) throw new OperationCanceledException("Code entry cancelled. No patch files were changed.");
                if(GateMatches(password,weekly) || GateMatches(password,recovery)) {
                    try { Directory.CreateDirectory(Config.StateDir); File.WriteAllText(GateMemoryPath,memory); } catch(IOException) { } catch(UnauthorizedAccessException) { }
                    return;
                }
            }
            throw new InvalidDataException("Incorrect code. Try Update again with this week's code.");
        }
        public byte[] Get(string commit,string repoPath) {
            if(repoPath=="index.json") {
                if(commit==unlockedCommit && unlockedIndex!=null) return unlockedIndex;
                byte[] raw=inner.Get(commit,repoPath);
                var outer=StrictJson.Read(raw,(int)Safety.MaxIndexBytes);
                object locked;
                if(!outer.TryGetValue("locked",out locked) || !(locked is bool) || !(bool)locked) {
                    objects=null;
                    object gate;
                    if(outer.TryGetValue("gate",out gate) && gate!=null) {
                        CheckGate(gate as Dictionary<string,object>);
                        unlockedIndex=raw; unlockedCommit=commit;   // one code entry per Update, even if index.json is read again
                    }
                    return raw;
                }
                if(Convert.ToInt32(outer["format"])!=3 || Convert.ToInt32(outer["iterations"])!=600000) throw new InvalidDataException("Unsupported encrypted publication. Update the launcher.");
                string number=outer["pass_number"] as string;
                if(number==null || !Regex.IsMatch(number,@"\A0\.[1-9][0-9]{0,8}\z")) throw new InvalidDataException("Invalid password number.");
                byte[] key=null;
                for(int attempt=0;attempt<3 && key==null;attempt++) {
                    string password=ask(number,attempt>0);
                    if(password==null) throw new OperationCanceledException("Password entry cancelled. No patch files were changed.");
                    key=CatalogKey(outer,password);
                }
                if(key==null) throw new InvalidDataException("Incorrect password. Try Update again with this week's code or your recovery password.");
                Dictionary<string,object> catalog;
                try { catalog=StrictJson.Read(Open(B64(outer["catalog"]),key),(int)Safety.MaxIndexBytes); }
                finally { Array.Clear(key,0,key.Length); }
                var candidate=(Dictionary<string,object>)catalog["objects"];
                foreach(var kv in candidate) {
                    Safety.Relative(kv.Key);
                    var obj=(Dictionary<string,object>)kv.Value;
                    string path=obj["path"] as string;
                    if(path==null || !Regex.IsMatch(path,@"\Alocked/[a-f0-9]{32}\.vlock\z") || B64(obj["key"]).Length!=64 || !Safety.IsSha(obj["sha"] as string)) throw new InvalidDataException("Invalid encrypted object.");
                    long size=Convert.ToInt64(obj["size"]);
                    if(size<68 || size>Safety.MaxAssetBytes+68) throw new InvalidDataException("Invalid encrypted object size.");
                }
                var js=new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
                byte[] index=Encoding.UTF8.GetBytes(js.Serialize(catalog["index"]));
                if(index.Length>Safety.MaxIndexBytes) throw new InvalidDataException("Decrypted catalog is too large.");
                objects=candidate; unlockedIndex=index; unlockedCommit=commit;
                return index;
            }
            if(objects==null) return inner.Get(commit,repoPath);
            if(commit!=unlockedCommit) throw new InvalidDataException("Encrypted catalog commit changed.");
            Directory.CreateDirectory(Config.StateDir);
            string temp=Path.Combine(Config.StateDir,"encrypted-"+Guid.NewGuid().ToString("N")+".vtmp");
            try { Download(commit,repoPath,temp,Safety.MaxMetadataBytes,delegate{}); return File.ReadAllBytes(temp); }
            finally { Safety.DeleteTemp(temp); }
        }
        public void Download(string commit,string repoPath,string dest,long maxBytes,Action<long> progress) {
            if(objects==null) { inner.Download(commit,repoPath,dest,maxBytes,progress); return; }
            object value;
            if(commit!=unlockedCommit || !objects.TryGetValue(repoPath,out value)) throw new InvalidDataException("Encrypted file is absent from the unlocked catalog.");
            var obj=(Dictionary<string,object>)value;
            string temp=dest+".encrypted";
            Safety.NoLinks(temp); Safety.NoLinks(dest);
            try {
                long size=Convert.ToInt64(obj["size"]);
                if(size>maxBytes+68) throw new InvalidDataException("Encrypted file exceeds its declared limit.");
                inner.Download(commit,(string)obj["path"],temp,size,progress);
                if(new FileInfo(temp).Length!=size || Updater.BlobSha(temp)!=(string)obj["sha"]) throw new InvalidDataException("Encrypted file checksum mismatch.");
                using(var input=new FileStream(temp,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    Decrypt(input,output,B64(obj["key"]),maxBytes);
            } catch { Safety.DeleteTemp(dest); throw; }
            finally { Safety.DeleteTemp(temp); }
        }
        internal static string Prompt(IWin32Window owner,string number,bool retry) {
            using(var form=PatchDialogs.Window("Weekly patch password",180)) {
                form.ClientSize=new Size(470,180);
                var title=new Label { Text=retry?"That code did not match. Try again:":"Enter this week's patch password:",Location=new Point(20,15),Size=new Size(425,24) };
                var version=new Label { Text="Pass "+number,Font=new Font("Segoe UI",8),Location=new Point(20,43),Size=new Size(420,18) };
                var box=new TextBox { UseSystemPasswordChar=true,Location=new Point(20,70),Size=new Size(425,26) };
                var ok=new Button { Text="Unlock",DialogResult=DialogResult.OK,Location=new Point(250,118),Size=new Size(95,32) };
                var cancel=new Button { Text="Cancel",DialogResult=DialogResult.Cancel,Location=new Point(350,118),Size=new Size(95,32) };
                form.Controls.AddRange(new Control[]{title,version,box,ok,cancel});form.AcceptButton=ok;form.CancelButton=cancel;
                form.Shown+=delegate { box.Focus(); };
                return form.ShowDialog(owner)==DialogResult.OK?box.Text:null;
            }
        }
    }
}
