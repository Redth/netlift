namespace NetLift.AndroidHost;

using Android.App;
using Android.OS;
using Android.Widget;

[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this)
        {
            Text = "NetLift .NET Android CoreCLR control"
        });
    }
}