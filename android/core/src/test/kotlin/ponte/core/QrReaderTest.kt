package ponte.core

import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import kotlin.math.cos
import kotlin.math.sin
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

class QrReaderTest {
    private val link = "https://filabox3d.com.br/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl"
    private val width = 1280
    private val height = 720
    private val stride = 1344   // as câmeras costumam deixar bytes sobrando no fim de cada linha

    /**
     * Uma imagem como a câmera vê o QR na tela do computador: girado, sem estar no centro, um pouco borrado, com
     * ruído e com o fundo mais claro de um lado. O QR é o mesmo do Ponte do Windows (modo byte, correção nível M).
     */
    private fun cameraFrame(text: String?, angleDegrees: Double, sizePx: Double, centerX: Double, centerY: Double): ByteArray {
        val random = Random(42)
        val modules = text?.let {
            QRCodeWriter().encode(it, BarcodeFormat.QR_CODE, 0, 0,
                mapOf(EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M, EncodeHintType.MARGIN to 4))
        }
        val angle = Math.toRadians(angleDegrees)
        val sharp = Array(height) { IntArray(width) }
        for (y in 0 until height) for (x in 0 until width) {
            var value = 150 + 50 * x / width   // a luz da sala
            if (modules != null) {
                // De volta para a posição no QR: desfaz a rotação e a escala.
                val dx = x - centerX
                val dy = y - centerY
                val u = (dx * cos(-angle) - dy * sin(-angle)) / sizePx * modules.width + modules.width / 2.0
                val v = (dx * sin(-angle) + dy * cos(-angle)) / sizePx * modules.height + modules.height / 2.0
                if (u >= 0 && v >= 0 && u < modules.width && v < modules.height)
                    value = if (modules[u.toInt(), v.toInt()]) 45 else 215
            }
            sharp[y][x] = value
        }
        val frame = ByteArray(stride * height) { random.nextInt(256).toByte() }
        for (y in 0 until height) for (x in 0 until width) {
            var sum = 0
            var count = 0
            for (oy in -1..1) for (ox in -1..1) {
                val sy = y + oy
                val sx = x + ox
                if (sy in 0 until height && sx in 0 until width) {
                    sum += sharp[sy][sx]
                    count++
                }
            }
            frame[y * stride + x] = (sum / count + random.nextInt(-12, 13)).coerceIn(0, 255).toByte()
        }
        return frame
    }

    @Test
    fun leOLinkDaTransmissaoNumaFotoDaTela() {
        assertEquals(link, QrReader.read(cameraFrame(link, 17.0, 330.0, 700.0, 380.0), width, height, stride))
        assertEquals(link, QrReader.read(cameraFrame(link, 90.0, 260.0, 400.0, 300.0), width, height, stride))
        assertEquals(link, QrReader.read(cameraFrame(link, -35.0, 520.0, 640.0, 360.0), width, height, stride))
    }

    @Test
    fun semQrNaoLeNada() {
        assertNull(QrReader.read(cameraFrame(null, 0.0, 0.0, 0.0, 0.0), width, height, stride))
    }

    @Test
    fun recusaTamanhoQueNaoBateComAImagem() {
        assertFailsWith<IllegalArgumentException> { QrReader.read(ByteArray(100), 20, 20) }
        assertFailsWith<IllegalArgumentException> { QrReader.read(ByteArray(400), 20, 20, 10) }
    }
}
