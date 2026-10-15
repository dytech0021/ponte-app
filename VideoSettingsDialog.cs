namespace Ponte;

/// <summary>Preferências solicitadas para a transmissão, separadas do modo realmente negociado.</summary>
internal sealed class VideoSettingsDialog : Form
{
    private readonly ComboBox _fps, _codec;
    private readonly CheckBox _chroma444, _ultraLowLatency;
    private readonly NumericUpDown _bitrate;
    private readonly TextBox _status;
    private static readonly int[] FpsValues = { 20, 30, 60, 120 };
    private static readonly string[] CodecValues = { "jpeg", "h264", "h265" };

    public VideoOptions Options => new VideoOptions(
        FpsValues[_fps.SelectedIndex], CodecValues[_codec.SelectedIndex],
        _chroma444.Checked, _ultraLowLatency.Checked, (int)_bitrate.Value).Normalize();

    public VideoSettingsDialog(VideoOptions options, string status)
    {
        options = options.Normalize();
        Ui.Prepare(this, "Vídeo - Ponte", 580, 536);

        Ui.Text(this, "FPS alvo", 16, 18, 142);
        _fps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(166, 14), Size = new Size(190, 23) };
        _fps.Items.AddRange(new object[] { "Atual (20 FPS)", "30 FPS", "60 FPS", "120 FPS" });
        _fps.SelectedIndex = Array.IndexOf(FpsValues, options.Fps);
        Controls.Add(_fps);
        Ui.Text(this, "É a meta. Os FPS recebidos de verdade aparecem na barra da sessão e dependem dos dois computadores, da rede e do monitor de lá (um monitor de 60 Hz não passa de 60).",
            16, 48, 548, 54);

        Ui.Text(this, "Codec", 16, 116, 142);
        _codec = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(166, 112), Size = new Size(398, 23) };
        _codec.Items.AddRange(new object[] { "JPEG (Compatível)", "H.264 (GPU)", "H.265 (GPU)" });
        _codec.SelectedIndex = Array.IndexOf(CodecValues, options.Codec);
        Controls.Add(_codec);
        Ui.Text(this, "H.264 e H.265 são codificados pela placa de vídeo do outro computador (NVIDIA, AMD ou Intel). Na primeira vez, cada computador baixa o componente de vídeo do servidor (42 MB). Sem suporte, a sessão segue em JPEG e o motivo aparece abaixo.",
            16, 146, 548, 46);

        _chroma444 = new CheckBox { Text = "Chroma 4:4:4", Location = new Point(16, 198), Size = new Size(548, 24), Checked = options.Chroma444 };
        Controls.Add(_chroma444);
        Ui.Text(this, "Cores sem perda de resolução. Pela placa só em NVIDIA (ou Intel no H.265); sem isso, PNG sem perdas, que gasta mais rede.", 36, 226, 528, 38);
        _ultraLowLatency = new CheckBox { Text = "Ultra baixa latência (menor atraso; perde um pouco de qualidade em movimento)", Location = new Point(16, 270), Size = new Size(548, 24), Checked = options.UltraLowLatency };
        Controls.Add(_ultraLowLatency);

        Ui.Text(this, "Bitrate (Mbps)", 16, 310, 142);
        _bitrate = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 5, Value = options.BitrateMbps, Location = new Point(166, 306), Size = new Size(110, 23) };
        Controls.Add(_bitrate);
        Ui.Text(this, "Limite de banda do H.264/H.265, de 5 a 100 Mbps. Com a tela parada quase nada é enviado. O JPEG usa o menu Qualidade.",
            16, 340, 548, 40);

        Ui.Text(this, "Em uso nesta sessão", 16, 394, 548).ForeColor = Ui.Accent;
        _status = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 418), Size = new Size(548, 62), TabStop = true };
        Controls.Add(_status);
        SetVideoStatus(status);

        var apply = Ui.Button(this, "Aplicar", 376, 496, 90, DialogResult.OK);
        var cancel = Ui.Button(this, "Cancelar", 474, 496, 90, DialogResult.Cancel);
        AcceptButton = apply;
        CancelButton = cancel;
    }

    public void SetVideoStatus(string status) => _status.Text = string.IsNullOrWhiteSpace(status) ? "Aguardando o status de vídeo do outro computador." : status;
}
