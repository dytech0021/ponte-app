using System.Drawing.Imaging;

namespace Ponte;

/// <summary>
/// Os codificadores de imagem do Windows, prontos antes de qualquer uso em paralelo (1.17.5).
/// O Save(…, ImageFormat) do .NET consulta uma tabela interna que ele monta no primeiro uso, e a publica antes de
/// preenchê-la: duas linhas de execução salvando a primeira imagem ao mesmo tempo, e uma delas recebia "codificador
/// nulo" (ArgumentNullException, parâmetro 'encoder'). Acontecia no começo de uma sessão em 4:4:4, quando a imagem e o
/// ponteiro do mouse salvam PNG juntos, e acontecia sempre com a tela codificada em faixas. Aqui a tabela é montada
/// uma vez, numa linha só, e quem codifica em paralelo usa o codificador direto, sem passar por ela.
/// </summary>
internal static class Codecs
{
    public static readonly ImageCodecInfo Jpeg, Png;

    static Codecs()
    {
        var all = ImageCodecInfo.GetImageEncoders();
        Jpeg = all.First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        Png = all.First(c => c.FormatID == ImageFormat.Png.Guid);
        using var one = new Bitmap(1, 1, PixelFormat.Format32bppRgb);
        using var sink = new MemoryStream();
        one.Save(sink, ImageFormat.Png);
    }

    /// <summary>Garante que o construtor acima já rodou. Chamado na partida do programa e no começo de cada sessão.</summary>
    public static void Ready() { }
}
