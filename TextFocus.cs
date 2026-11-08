using System.Runtime.InteropServices;

namespace Ponte;

/// <summary>
/// 1.17.1: diz se o foco do teclado está num campo de texto, para o celular de quem acessa abrir o teclado sozinho.
/// Pergunta à Automação da Interface do Windows (vale para Chrome, Edge, Office, programas novos e antigos); se ela não
/// souber, vale o cursor de texto do Windows (o traço piscando), que os programas antigos e o Chrome criam.
/// </summary>
internal static class TextFocus
{
    private const int ControlTypeProperty = 30003, IsPasswordProperty = 30019, IsValuePatternAvailableProperty = 30043, ValueIsReadOnlyProperty = 30046;
    private const int EditControl = 50004, DocumentControl = 50030;

    [ThreadStatic] private static IUIAutomation? _automation;
    [ThreadStatic] private static bool _automationFailed;

    /// <summary>Há um campo de texto com o foco agora. Chamar sempre da mesma linha (MTA) dedicada.</summary>
    public static bool Focused()
    {
        bool? fromAutomation = null;
        try { fromAutomation = FromAutomation(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { }
        return fromAutomation == true || SystemCaret();
    }

    private static bool? FromAutomation()
    {
        if (_automationFailed) return null;
        if (_automation is null)
        {
            try { _automation = (IUIAutomation)new CUIAutomation(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _automationFailed = true;
                return null;
            }
        }
        if (_automation.GetFocusedElement(out var element) != 0 || element is null) return null;
        try
        {
            int type = Property(element, ControlTypeProperty) is int t ? t : 0;
            if (Property(element, IsPasswordProperty) is true) return true;
            bool hasValue = Property(element, IsValuePatternAvailableProperty) is true;
            bool readOnly = hasValue && Property(element, ValueIsReadOnlyProperty) is true;
            // Caixa de texto: só não conta se for só de leitura. Documento (Bloco de Notas novo, Word, página da web):
            // só conta se for editável, senão clicar em qualquer ponto de uma página abriria o teclado.
            return type switch
            {
                EditControl => !readOnly,
                DocumentControl => hasValue && !readOnly,
                _ => null,
            };
        }
        finally { Marshal.ReleaseComObject(element); }
    }

    private static object? Property(IUIAutomationElement element, int id) =>
        element.GetCurrentPropertyValue(id, out object? value) == 0 ? value : null;

    // O traço piscando do Windows na janela em primeiro plano.
    private static bool SystemCaret()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        uint thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) return false;
        return info.Caret != 0 && (info.Flags & 1) != 0; // GUI_CARETBLINKING
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    // Só os métodos usados são chamados; os anteriores existem para manter a ordem da tabela da interface.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object? value);
    }
}
