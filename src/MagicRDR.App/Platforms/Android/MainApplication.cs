using Android.App;
using Android.Runtime;

namespace MagicRDR.App;

// LargeHeap: RPF tables of contents and compression buffers can be sizeable on big archives.
[Application(LargeHeap = true)]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
