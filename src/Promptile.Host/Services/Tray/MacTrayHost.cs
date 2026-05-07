using System.Runtime.InteropServices;

namespace Promptile.Host.Services.Tray;

/// <summary>
/// macOS menu bar icon via raw P/Invoke to the Objective-C runtime.
/// </summary>
public class MacTrayHost : ITrayHost
{
    private IntPtr _statusItem;
    private IntPtr _statusMenuItem;
    private IntPtr _nsApp;
    private Action? _onQuit;

    private const string LibObjC = "/usr/lib/libobjc.dylib";
    private const string LibDL   = "/usr/lib/libdl.dylib";

    [DllImport(LibDL)] static extern IntPtr dlopen(string path, int mode);
    [DllImport(LibDL)] static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    static extern int LSRegisterURL(IntPtr cfUrl, bool update);
    // dispatch_* loaded manually via dlsym — DllImport can't find libdispatch via the short name
    // because on macOS 12+ the file only exists in the dyld shared cache at the full system path.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DispatchFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DispatchAsyncFFn(IntPtr queue, IntPtr ctx, IntPtr fn);
    private static DispatchFn?       _requestAuthFn;   // keep alive
    private static DispatchAsyncFFn? _dispatchAsyncF;  // keep alive
    private static IntPtr            _mainQueue;
    [DllImport(LibObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, long a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, double a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void   SendVoid(IntPtr self, IntPtr sel, ulong a, IntPtr b);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void   SendVoid(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(LibObjC)] static extern IntPtr objc_allocateClassPair(IntPtr super_, string name, int extra);
    [DllImport(LibObjC)] static extern void   objc_registerClassPair(IntPtr cls);
    [DllImport(LibObjC)] static extern bool   class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ActionCallback(IntPtr self, IntPtr sel, IntPtr sender);

    // ObjC block used as a no-op completion handler for requestAuthorization
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void BoolErrorBlock(IntPtr block, byte granted, IntPtr error);

    [StructLayout(LayoutKind.Sequential)]
    struct BlockDescriptor { public ulong Reserved; public ulong Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct BlockLiteral { public IntPtr Isa; public int Flags; public int Rsv; public IntPtr Invoke; public IntPtr Descriptor; }

    private static readonly List<ActionCallback> _callbacks = [];

    // Pinned forever — the ObjC runtime holds a pointer into these
    private static readonly BoolErrorBlock _authInvoke = static (_, granted, _) =>
        Console.WriteLine($"[Notifications] Permission granted: {granted != 0}");
    private static GCHandle _authDescPin, _authBlockPin;
    private static IntPtr   _authBlockPtr;

    private static IntPtr EnsureAuthBlock()
    {
        if (_authBlockPtr != IntPtr.Zero) return _authBlockPtr;
        var desc = new BlockDescriptor { Reserved = 0, Size = (ulong)Marshal.SizeOf<BlockLiteral>() };
        _authDescPin = GCHandle.Alloc(desc, GCHandleType.Pinned);
        var globalBlockIsa = dlsym(new IntPtr(-2), "_NSConcreteGlobalBlock");
        var lit = new BlockLiteral
        {
            Isa     = globalBlockIsa,
            Flags   = 1 << 28, // BLOCK_IS_GLOBAL
            Rsv     = 0,
            Invoke  = Marshal.GetFunctionPointerForDelegate(_authInvoke),
            Descriptor = _authDescPin.AddrOfPinnedObject(),
        };
        _authBlockPin = GCHandle.Alloc(lit, GCHandleType.Pinned);
        _authBlockPtr = _authBlockPin.AddrOfPinnedObject();
        return _authBlockPtr;
    }

    private static IntPtr NSStr(string s)
    {
        var ptr   = Marshal.StringToHGlobalAnsi(s);
        var nsStr = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), ptr);
        Marshal.FreeHGlobal(ptr);
        return nsStr;
    }

    public void Run(Action onQuit)
    {
        _onQuit = onQuit;

        Console.WriteLine($"[Notifications] Process path: {Environment.ProcessPath}");

        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);
        dlopen("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", 1);

        _nsApp = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        Send(_nsApp, sel_registerName("setActivationPolicy:"), 1L);

        // Register with Launch Services
        var mainBundle = Send(objc_getClass("NSBundle"), sel_registerName("mainBundle"));
        LSRegisterURL(Send(mainBundle, sel_registerName("bundleURL")), true);

        // Load libdispatch manually — DllImport short-name resolution misses the system path.
        // _dispatch_main_q is the main-queue struct; its address is the dispatch_queue_t handle.
        if (_dispatchAsyncF == null)
        {
            var lib = dlopen("/usr/lib/system/libdispatch.dylib", 1);
            _mainQueue   = dlsym(lib, "_dispatch_main_q");
            var asyncFPtr = dlsym(lib, "dispatch_async_f");
            _dispatchAsyncF = Marshal.GetDelegateForFunctionPointer<DispatchAsyncFFn>(asyncFPtr);
        }

        // Request notification permission on the main queue after the run loop starts.
        // dispatch_async_f defers the block until [NSApp run] has started the run loop,
        // which is required before macOS will show the permission dialog.
        _requestAuthFn = _ =>
        {
            var center = Send(objc_getClass("UNUserNotificationCenter"),
                sel_registerName("currentNotificationCenter"));
            Console.WriteLine("[Notifications] Requesting auth on main queue");
            SendVoid(center,
                sel_registerName("requestAuthorizationWithOptions:completionHandler:"),
                6UL, EnsureAuthBlock());
        };
        _dispatchAsyncF(_mainQueue, IntPtr.Zero,
            Marshal.GetFunctionPointerForDelegate(_requestAuthFn));

        var statusBar = Send(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
        _statusItem = Send(statusBar, sel_registerName("statusItemWithLength:"), -1.0);
        Send(_statusItem, sel_registerName("retain"));

        var button = Send(_statusItem, sel_registerName("button"));
        Send(button, sel_registerName("setTitle:"), NSStr("P"));

        var menu = Send(Send(objc_getClass("NSMenu"), sel_registerName("alloc")), sel_registerName("init"));

        var delegateCls = objc_allocateClassPair(objc_getClass("NSObject"), "TrayMenuDelegate", 0);

        _statusMenuItem = CreateMenuItem(menu, "Ready", IntPtr.Zero, IntPtr.Zero);
        Send(_statusMenuItem, sel_registerName("setEnabled:"), IntPtr.Zero);

        AddSeparator(menu);

        var openDashboardSel = sel_registerName("openDashboard:");
        ActionCallback openDashboardCb = (_, _, _) => OpenUrl("http://localhost:5309");
        _callbacks.Add(openDashboardCb);
        class_addMethod(delegateCls, openDashboardSel, Marshal.GetFunctionPointerForDelegate(openDashboardCb), "v@:@");

        var quitSel = sel_registerName("quitTray:");
        ActionCallback quitCb = (_, _, _) =>
        {
            _onQuit?.Invoke();
            Send(_nsApp, sel_registerName("terminate:"), IntPtr.Zero);
        };
        _callbacks.Add(quitCb);
        class_addMethod(delegateCls, quitSel, Marshal.GetFunctionPointerForDelegate(quitCb), "v@:@");

        objc_registerClassPair(delegateCls);

        var delegateInstance = Send(Send(delegateCls, sel_registerName("alloc")), sel_registerName("init"));

        CreateMenuItem(menu, "Open Dashboard", openDashboardSel, delegateInstance);
        AddSeparator(menu);
        CreateMenuItem(menu, "Quit", quitSel, delegateInstance);

        Send(_statusItem, sel_registerName("setMenu:"), menu);

        StartPolling();

        Send(_nsApp, sel_registerName("run"));
    }

    public void UpdateStatus(string label)
    {
        if (_statusMenuItem == IntPtr.Zero) return;
        Send(_statusMenuItem, sel_registerName("setTitle:"), NSStr(label));
    }

    public void Shutdown()
    {
        Send(_nsApp, sel_registerName("terminate:"), IntPtr.Zero);
    }

    public void ShowNotification(string title, string message)
    {
        Console.WriteLine($"[Notifications] ShowNotification: {title} — {message}");
        dlopen("/System/Library/Frameworks/UserNotifications.framework/UserNotifications", 1);

        var center = Send(objc_getClass("UNUserNotificationCenter"), sel_registerName("currentNotificationCenter"));

        var content = Send(
            Send(objc_getClass("UNMutableNotificationContent"), sel_registerName("alloc")),
            sel_registerName("init"));
        Send(content, sel_registerName("setTitle:"), NSStr(title));
        Send(content, sel_registerName("setBody:"),  NSStr(message));
        var sound = Send(objc_getClass("UNNotificationSound"), sel_registerName("defaultSound"));
        Send(content, sel_registerName("setSound:"), sound);

        var request = Send(
            objc_getClass("UNNotificationRequest"),
            sel_registerName("requestWithIdentifier:content:trigger:"),
            NSStr(Guid.NewGuid().ToString()), content, IntPtr.Zero);

        // completionHandler is nullable — pass null
        SendVoid(center, sel_registerName("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
    }

    private void StartPolling()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            while (true)
            {
                try
                {
                    var resp = await http.GetStringAsync("http://localhost:5309/api/tray/status");
                    var doc  = System.Text.Json.JsonDocument.Parse(resp);
                    var labels = new List<string>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var label = item.GetProperty("label").GetString();
                        if (!string.IsNullOrEmpty(label)) labels.Add(label);
                    }
                    var combined = labels.Count > 0 ? string.Join(" | ", labels) : "Ready";
                    Send(_statusMenuItem, sel_registerName("setTitle:"), NSStr(combined));
                }
                catch { }

                await Task.Delay(10000);
            }
        });
    }

    private static IntPtr CreateMenuItem(IntPtr menu, string title, IntPtr action, IntPtr target)
    {
        var item = Send(
            Send(objc_getClass("NSMenuItem"), sel_registerName("alloc")),
            sel_registerName("initWithTitle:action:keyEquivalent:"),
            NSStr(title), action, NSStr(""));
        if (target != IntPtr.Zero)
            Send(item, sel_registerName("setTarget:"), target);
        Send(menu, sel_registerName("addItem:"), item);
        return item;
    }

    private static void AddSeparator(IntPtr menu)
    {
        var sep = Send(objc_getClass("NSMenuItem"), sel_registerName("separatorItem"));
        Send(menu, sel_registerName("addItem:"), sep);
    }

    private static void OpenUrl(string url)
    {
        System.Diagnostics.Process.Start("open", url);
    }

}
