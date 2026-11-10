namespace Ponte;

/// <summary>
/// Escolha do atalho que troca de tela numa sessão (1.18.0): a pessoa aperta a combinação e ela aparece escrita. Precisa
/// de Ctrl, Alt ou Shift junto, ou ser uma tecla F, para não roubar uma tecla que se digita no outro computador.
/// </summary>
internal sealed class SwitchKeysDialog : Form
{
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly Button _ok;

    /// <summary>A combinação escolhida; Keys.None é sem atalho.</summary>
    public Keys Combo { get; private set; }

    public SwitchKeysDialog(Keys current)
    {
        Combo = current;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Atalho para trocar de tela";
        Icon = Ui.MakeIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 190);

        var text = new Label
        {
            Text = "Com o computador acessado tendo mais de uma tela, esta combinação passa para a próxima tela " +
                   "(e não é enviada para lá). Clique na caixa e aperte a combinação:",
            Location = new Point(14, 12), Size = new Size(412, 54),
        };
        _box = new TextBox { Location = new Point(14, 70), Width = 412, ReadOnly = true, TabStop = true, Font = new Font(Font.FontFamily, 11f) };
        _box.Text = current == Keys.None ? "" : Describe(current);
        _hint = new Label { Location = new Point(14, 104), Size = new Size(412, 36), ForeColor = SystemColors.GrayText,
            Text = "Exemplos: Ctrl + Alt + Seta para a direita, Ctrl + Shift + M, F9." };
        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(176, 148), Size = new Size(80, 30) };
        var none = new Button { Text = "Sem atalho", Location = new Point(262, 148), Size = new Size(84, 30) };
        none.Click += (_, _) => { Combo = Keys.None; DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(352, 148), Size = new Size(74, 30) };
        Controls.AddRange(new Control[] { text, _box, _hint, _ok, none, cancel });
        CancelButton = cancel;
        Shown += (_, _) => _box.Focus();
    }

    // Enquanto a caixa está com o foco, toda tecla vira a combinação (Tab, Enter e Esc também, para poder escolhê-las
    // com Ctrl ou Alt); sem modificadora, Esc fecha e Enter confirma, como em qualquer janela.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_box.Focused) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
        {
            _box.Text = Describe(modifiers) + " + ...";
            return true;
        }
        if (modifiers == Keys.None && key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (modifiers == Keys.None && key == Keys.Enter) { DialogResult = DialogResult.OK; return true; }
        if (!Allowed(keyData))
        {
            _hint.Text = "Use Ctrl, Alt ou Shift junto com a tecla (ou uma tecla F sozinha). Shift sozinho com letra ou número não vale: é como se digita.";
            _hint.ForeColor = Ui.Danger;
            return true;
        }
        Combo = keyData;
        _box.Text = Describe(keyData);
        _hint.Text = "Aperte OK para guardar.";
        _hint.ForeColor = SystemColors.GrayText;
        _ok.Focus();
        return true;
    }

    /// <summary>Se a combinação serve de atalho sem atrapalhar o que se digita lá.</summary>
    internal static bool Allowed(Keys combo)
    {
        var key = combo & Keys.KeyCode;
        var modifiers = combo & (Keys.Control | Keys.Shift | Keys.Alt);
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return false;
        bool function = key is >= Keys.F1 and <= Keys.F24;
        if (modifiers == Keys.None) return function;
        // Shift + letra, número ou sinal é só uma maiúscula ou um símbolo.
        if (modifiers == Keys.Shift) return !(key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or Keys.Space || key >= Keys.OemSemicolon);
        return true;
    }

    /// <summary>A combinação por extenso, como aparece no menu: "Ctrl + Alt + Seta para a direita".</summary>
    internal static string Describe(Keys combo)
    {
        var parts = new List<string>();
        if (combo.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (combo.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (combo.HasFlag(Keys.Shift)) parts.Add("Shift");
        var key = combo & Keys.KeyCode;
        if (key != Keys.None) parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"{key - Keys.NumPad0} (teclado numérico)",
        Keys.Left => "Seta para a esquerda", Keys.Right => "Seta para a direita",
        Keys.Up => "Seta para cima", Keys.Down => "Seta para baixo",
        Keys.PageUp => "Page Up", Keys.PageDown => "Page Down",
        Keys.Space => "Espaço", Keys.Enter => "Enter", Keys.Escape => "Esc", Keys.Tab => "Tab", Keys.Back => "Backspace",
        Keys.Insert => "Insert", Keys.Delete => "Delete", Keys.Home => "Home", Keys.End => "End",
        Keys.Pause => "Pause", Keys.Scroll => "Scroll Lock", Keys.Oemtilde => "'", Keys.OemMinus => "-", Keys.Oemplus => "=",
        Keys.Oemcomma => ",", Keys.OemPeriod => ".",
        _ => key.ToString(),
    };
}
