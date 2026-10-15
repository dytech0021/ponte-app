package ponte.core

import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader

/**
 * Lê o QR Code de uma imagem da câmera. Achar o código numa foto (torto, de longe, numa tela de computador) é a parte
 * difícil, e por isso esta é a única biblioteca de fora do aplicativo: a ZXing, só o núcleo em Java (Apache 2.0).
 */
object QrReader {
    private val hints = mapOf(DecodeHintType.CHARACTER_SET to "UTF-8")

    /**
     * O texto do QR, ou null se não há um legível. [luminance] é o brilho de cada pixel, um byte por pixel, linha após
     * linha, com [rowStride] bytes entre o começo de uma linha e o da seguinte (o plano Y do Android, como vem).
     */
    fun read(luminance: ByteArray, width: Int, height: Int, rowStride: Int = width): String? {
        require(width > 0 && height > 0 && rowStride >= width) { "Tamanho de imagem inválido." }
        require(luminance.size >= rowStride * (height - 1) + width) { "A imagem é menor que o tamanho informado." }
        val source = PlanarYUVLuminanceSource(luminance, rowStride, height, 0, 0, width, height, false)
        return try {
            QRCodeReader().decode(BinaryBitmap(HybridBinarizer(source)), hints).text
        } catch (e: ReaderException) {
            null
        }
    }
}
