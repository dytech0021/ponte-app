using System.Drawing.Drawing2D;

namespace Ponte;

/// <summary>
/// Lista dos computadores salvos e recentes. Cada um aparece com a última imagem vista da tela dele, que é
/// o que mais ajuda a reconhecê-lo. O desenho é feito aqui para sair igual no tema claro e no escuro.
/// </summary>
internal sealed class ComputerList : ListBox
{
    /// <summary>Título de um grupo; não é selecionável.</summary>
    public sealed record Header(string Text);

    /// <summary>
    /// Um computador da lista. Source é a entrada da configuração (salvo ou recente). PresenceKey ("id:..." ou "ip:...")
    /// é de quem mostra a bolinha de online; os recentes não têm.
    /// </summary>
    public sealed record Entry(object Source, string Name, string Address, string Detail, string Hint, Image Thumbnail, string? PresenceKey = null);

    private readonly ToolTip _tip = new();
    private readonly Size _thumbnail;
    private readonly int _pad;
    private int _lastIndex = -1, _hover = -1;
    private IReadOnlyDictionary<string, bool> _online = new Dictionary<string, bool>();

    /// <summary>Duplo clique num computador.</summary>
    public event Action<Entry>? Activated;

    public Size ThumbnailSize => _thumbnail;
    public Entry? Selected => SelectedItem as Entry;

    /// <summary>Quem está online agora, por chave. Vale até a próxima consulta, mesmo com a lista refeita.</summary>
    public void SetOnline(IReadOnlyDictionary<string, bool> online)
    {
        _online = online;
        _hover = -1;
        Invalidate();
    }

    /// <summary>true online, false offline, null sem bolinha (recente, ou ainda não deu para saber).</summary>
    public bool? OnlineOf(Entry entry) => entry.PresenceKey is { } key && _online.TryGetValue(key, out bool on) ? on : null;

    public ComputerList()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        IntegralHeight = false;
        DoubleBuffered = true;
        _thumbnail = new Size(LogicalToDeviceUnits(112), LogicalToDeviceUnits(63));
        _pad = LogicalToDeviceUnits(5);
    }

    public void Fill(IEnumerable<object> items)
    {
        BeginUpdate();
        foreach (var old in Items.OfType<Entry>()) old.Thumbnail.Dispose();
        Items.Clear();
        _lastIndex = _hover = -1;
        foreach (object item in items) Items.Add(item);
        EndUpdate();
    }

    /// <summary>A miniatura gravada em <paramref name="path"/> ou, se não houver, o desenho de um monitor.</summary>
    public Image LoadThumbnail(string path)
    {
        var image = new Bitmap(_thumbnail.Width, _thumbnail.Height);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        try
        {
            if (File.Exists(path))
            {
                // Lida da memória, para o arquivo não ficar preso enquanto a lista existir.
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                using var saved = Image.FromStream(stream);
                g.DrawImage(saved, 0, 0, image.Width, image.Height);
                return image;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException) { }

        g.Clear(Ui.Dark ? Color.FromArgb(52, 56, 60) : Color.FromArgb(226, 231, 234));
        using var pen = new Pen(Ui.Dark ? Color.FromArgb(140, 150, 156) : Color.FromArgb(128, 140, 148), Math.Max(2f, image.Height / 28f));
        float w = image.Width * 0.34f, h = image.Height * 0.38f, x = (image.Width - w) / 2, y = image.Height * 0.24f, foot = y + h + image.Height * 0.12f;
        g.DrawRectangle(pen, x, y, w, h);
        g.DrawLine(pen, image.Width / 2f, y + h, image.Width / 2f, foot);
        g.DrawLine(pen, image.Width / 2f - w * 0.25f, foot, image.Width / 2f + w * 0.25f, foot);
        return image;
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        e.ItemHeight = Items[e.Index] is Header ? LogicalToDeviceUnits(26) : _thumbnail.Height + 2 * _pad;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var area = e.Bounds;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        if (Items[e.Index] is Header header)
        {
            using var back = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(back, area);
            var text = new Rectangle(area.X + _pad, area.Y, area.Width - 2 * _pad, area.Height);
            int width = TextRenderer.MeasureText(e.Graphics, header.Text, Font, text.Size, flags).Width;
            TextRenderer.DrawText(e.Graphics, header.Text, Font, text, Ui.Accent, flags);
            using var line = new Pen(Ui.Dark ? Color.FromArgb(70, 76, 80) : Color.FromArgb(205, 212, 217));
            int y = area.Y + area.Height / 2 + 1;
            e.Graphics.DrawLine(line, text.X + width + _pad, y, area.Right - _pad, y);
            return;
        }

        var entry = (Entry)Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? SystemColors.Highlight : BackColor)) e.Graphics.FillRectangle(back, area);
        e.Graphics.DrawImage(entry.Thumbnail, area.X + _pad, area.Y + _pad);

        Color main = selected ? SystemColors.HighlightText : ForeColor, soft = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        int left = area.X + _thumbnail.Width + 3 * _pad, line0 = Font.Height + LogicalToDeviceUnits(3);
        int top = area.Y + (area.Height - 3 * line0) / 2;
        Rectangle Line(int n) => new(left, top + n * line0, area.Right - left - _pad, line0);

        // A bolinha vem logo depois do nome; um nome comprido é cortado antes dela, para ela não sumir.
        var name = Line(0);
        bool? online = OnlineOf(entry);
        int size = LogicalToDeviceUnits(16), gap = LogicalToDeviceUnits(6);
        if (online is not null) name.Width = Math.Max(0, name.Width - size - gap);
        TextRenderer.DrawText(e.Graphics, entry.Name, Font, name, main, flags);
        if (online is { } on)
        {
            // Verde: o Ponte está aberto lá agora. Vermelho: não está (desligado, sem internet ou Ponte fechado).
            int written = Math.Min(name.Width, TextRenderer.MeasureText(e.Graphics, entry.Name, Font, name.Size, flags).Width);
            var dot = new Rectangle(name.X + written + gap, name.Y + (name.Height - size) / 2, size, size);
            var smooth = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(on ? Color.FromArgb(46, 186, 92) : Color.FromArgb(224, 62, 62))) e.Graphics.FillEllipse(fill, dot);
            e.Graphics.SmoothingMode = smooth;
        }
        TextRenderer.DrawText(e.Graphics, entry.Address, Font, Line(1), soft, flags);
        TextRenderer.DrawText(e.Graphics, entry.Detail, Font, Line(2), soft, flags);
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0) e.DrawFocusRectangle();
    }

    // O título de grupo não fica selecionado: a seleção pula para o computador seguinte na direção do movimento.
    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        int index = SelectedIndex;
        if (index >= 0 && Items[index] is Header)
        {
            int step = index < _lastIndex ? -1 : 1, next = index + step;
            while (next >= 0 && next < Items.Count && Items[next] is Header) next += step;
            SelectedIndex = next >= 0 && next < Items.Count ? next : _lastIndex;
            return;
        }
        _lastIndex = index;
        base.OnSelectedIndexChanged(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0 && index < Items.Count && Items[index] is Entry entry) Activated?.Invoke(entry);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexFromPoint(e.Location);
        if (index == _hover) return;
        _hover = index;
        _tip.SetToolTip(this, index >= 0 && index < Items.Count && Items[index] is Entry entry ? WithPresence(entry) : "");
    }

    private string WithPresence(Entry entry)
    {
        string presence = OnlineOf(entry) switch
        {
            true => "Online agora: o Ponte está aberto lá.",
            false => "Offline: o Ponte não está aberto lá (computador desligado, sem internet ou Ponte fechado).",
            null => "",
        };
        return string.Join(Environment.NewLine, new[] { presence, entry.Hint }.Where(t => t.Length > 0));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var entry in Items.OfType<Entry>()) entry.Thumbnail.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
