using System.Reflection;
using System.Runtime.InteropServices;

namespace Ponte;

/// <summary>
/// 1.17.4: abre um endereço no navegador padrão como o usuário, mesmo com o Ponte aberto como administrador. Pede à área de
/// trabalho do Windows (o Explorer, que roda como o usuário) que abra o endereço. Antes ia por "explorer.exe endereço", que
/// com endereços longos (o do login com Google) ou em alguns Windows abria o Explorador de Arquivos em vez do navegador.
/// </summary>
internal static class DesktopShell
{
    private const int SwcDesktop = 8, SwfoNeedDispatch = 1, SvgioBackground = 0, ShowNormal = 1;
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserId = typeof(IShellBrowser).GUID;
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");

    /// <summary>Abre pela área de trabalho; falso se não deu (sem Explorer rodando, por exemplo).</summary>
    public static bool Open(string url)
    {
        bool opened = false;
        // Os objetos da área de trabalho querem uma linha STA própria.
        var thread = new Thread(() =>
        {
            try { opened = OpenOnThisThread(url); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException or MissingMethodException or UnauthorizedAccessException) { }
        }) { IsBackground = true, Name = "ponte-abrir-navegador" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return opened;
    }

    private static bool OpenOnThisThread(string url)
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
        var windows = (IShellWindows)Activator.CreateInstance(type)!;
        try
        {
            object location = 0, root = null!; // CSIDL_DESKTOP
            object? desktop = windows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (desktop is not IServiceProvider services) return false;
            Guid sid = TopLevelBrowser, iid = ShellBrowserId;
            if (services.QueryService(ref sid, ref iid, out object? browserObject) != 0 || browserObject is not IShellBrowser browser) return false;
            if (browser.QueryActiveShellView(out var view) != 0 || view is null) return false;
            Guid dispatch = DispatchId;
            if (view.GetItemObject(SvgioBackground, ref dispatch, out object? folderView) != 0 || folderView is null) return false;
            // IShellFolderViewDual.Application → IShellDispatch2.ShellExecute, chamados pelo IDispatch.
            object? application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
            if (application is null) return false;
            application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application, [url, "", "", "open", ShowNormal]);
            return true;
        }
        finally { Marshal.ReleaseComObject(windows); }
    }

    // Só os métodos usados são chamados; os anteriores existem para manter a ordem da tabela da interface.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void get_Count();
        void Item();
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object location, ref object root, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [PreserveSig] int GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }
}
