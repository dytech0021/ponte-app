namespace Ponte;

/// <summary>
/// Como a pessoa quer receber a tela. O padrão é o modo de sempre (JPEG a 20 quadros por segundo), e quem
/// não conhece estas opções (Ponte anterior à 1.6 e o aplicativo do celular) nunca recebe vídeo.
/// </summary>
internal sealed record VideoOptions(int Fps = 20, string Codec = "jpeg", bool Chroma444 = false,
    bool UltraLowLatency = false, int BitrateMbps = 20)
{
    public VideoOptions Normalize() => this with
    {
        Fps = Fps is 20 or 30 or 60 or 120 ? Fps : 20,
        Codec = Codec is "h264" or "h265" ? Codec : "jpeg",
        BitrateMbps = Math.Clamp(BitrateMbps, 5, 100),
    };

    public static string CodecName(string codec) => codec switch { "h264" => "H.264", "h265" => "H.265", _ => "JPEG" };
}

/// <summary>
/// Perfil de transmissão de quem acessa: qualidade da imagem (JPEG) e opções de vídeo. Cada perfil começa com o
/// seu original e passa a guardar o que a pessoa mudar nele; a próxima conexão abre no último perfil usado.
/// </summary>
internal sealed record StreamProfile(int Quality = 60, VideoOptions? Video = null)
{
    public const string Default = "padrao";

    /// <summary>Os perfis, na ordem do menu: chave gravada na configuração e nome mostrado.</summary>
    public static readonly (string Key, string Name)[] All =
    {
        (Default, "Padrão"),
        ("baixa-latencia", "Baixa latência"),
        ("qualidade", "Qualidade"),
    };

    public static string NameOf(string key) => All.FirstOrDefault(p => p.Key == key).Name ?? "Padrão";

    /// <summary>Como cada perfil vem de fábrica. Padrão é o modo de sempre (JPEG, imagem média, 20 FPS).</summary>
    public static StreamProfile Original(string key) => key switch
    {
        "baixa-latencia" => new(60, new VideoOptions(30, "jpeg", UltraLowLatency: true)),
        "qualidade" => new(85, new VideoOptions(60, "jpeg")),
        _ => new(60, new VideoOptions()),
    };

    public StreamProfile Normalize() => this with { Quality = Math.Clamp(Quality, 10, 100), Video = (Video ?? new VideoOptions()).Normalize() };
}

/// <summary>
/// Começo de um vídeo. Cada troca de opções começa um vídeo novo, com outro Id; os pacotes trazem o Id,
/// e os de um vídeo anterior que ainda estiverem a caminho são ignorados.
/// </summary>
internal sealed record VideoStartMsg(int Id, int Width, int Height, string Codec, string Encoder,
    int Fps, bool Chroma444, bool UltraLowLatency, string Capture = "");

/// <summary>Do visualizador: o vídeo Id não pôde ser decodificado aqui, volte ao modo compatível.</summary>
internal sealed record VideoFeedbackMsg(int Id, bool Failed = false, string Reason = "");
