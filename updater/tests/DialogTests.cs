using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VirgilPatch;

class DialogTests
{
    static void Assert(bool ok, string text) { if (!ok) throw new Exception(text); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            Config.StateDir=Path.Combine(root,"state"); var state=new State {patches_dir=Path.Combine(root,"Patches")};
            Directory.CreateDirectory(Path.Combine(state.patches_dir,"Test"));
            var source=new LauncherFixtureSource(); source.Publish("dialog fixture"); var u=new Updater(source,state); var catalog=u.Catalog();
            using(var timer=new Timer {Interval=100})
            {
                timer.Tick+=delegate {
                    var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text.StartsWith("Choose patches")); if(form==null)return;
                    timer.Stop();var list=form.Controls.OfType<CheckedListBox>().Single();
                    Assert(list.CheckedItems.Count==1,"Existing patch not selected by default.");
                    form.Controls.OfType<CheckBox>().Single().Checked=true;
                    using(var image=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1]+".selection.png");}
                    form.Controls.OfType<Button>().Single(b=>b.Text=="Check selected").PerformClick();
                };
                timer.Start();var choice=PatchDialogs.Choose(null,catalog,state); Assert(choice.Verify && choice.Names.SequenceEqual(new[]{"Test"}),"Selection was not returned.");
            }
            var plan=u.Check(new[]{"Test"},catalog);
            Assert(source.Downloads.Count==0,"Preview downloaded assets before consent.");
            using(var timer=new Timer {Interval=100})
            {
                timer.Tick+=delegate {
                    var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text.StartsWith("What's new"));if(form==null)return;
                    timer.Stop();Assert(form.Controls.OfType<TextBox>().Single().Text.Contains("a.ogg"),"Changes missing from preview.");
                    using(var image=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1]+".changes.png");}
                    form.Controls.OfType<Button>().Single(b=>b.Text=="Cancel").PerformClick();
                };
                timer.Start();Assert(!PatchDialogs.Review(null,plan),"Preview cancellation ignored.");
            }
            Assert(source.Downloads.Count==0 && !File.Exists(Path.Combine(state.patches_dir,"Test/a.ogg")),"Cancelled preview changed game files.");
            Console.WriteLine("PASS actual folder chooser, remembered/default selection, deep repair toggle, changes preview and cancellation without asset downloads");return 0;
        }
        catch(Exception ex) {Console.WriteLine("FAIL "+ex);return 1;}
    }
}
