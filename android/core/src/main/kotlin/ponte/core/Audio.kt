package ponte.core

/** Um trecho do som do computador: amostras de 16 bits intercaladas (esquerda, direita). */
class Sound(val samples: ShortArray, val rate: Int, val channels: Int)

/**
 * O ponteiro do mouse do computador: a imagem dele (um PNG com transparência, dentro de [data]) e o
 * ponto da imagem que marca a posição do mouse.
 */
class PointerShape(val hotX: Int, val hotY: Int, val data: ByteArray, val offset: Int, val length: Int)

/**
 * Decodifica a mensagem Audio, como descrita em Audio.cs: formato (1 = PCM de 16 bits, 2 = ADPCM IMA
 * de 4 bits), canais, amostras por segundo e os dados. No ADPCM, antes dos dados vem o estado de cada
 * canal, e cada byte traz uma amostra de cada canal: o esquerdo nos 4 bits baixos.
 */
object AudioCodec {
    const val PCM = 1
    const val ADPCM = 2
    private const val HEADER = 6
    private const val MAX_PAYLOAD = 1024 * 1024

    private val INDEX_STEP = intArrayOf(-1, -1, -1, -1, 2, 4, 6, 8)
    private val STEPS = intArrayOf(
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
        143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
        1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
        9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    )

    private fun u8(p: ByteArray, at: Int) = p[at].toInt() and 0xFF
    private fun s16(p: ByteArray, at: Int) = (u8(p, at) or (p[at + 1].toInt() shl 8)).toShort()

    /** O som de uma mensagem, ou null se ela não serve. */
    fun decode(p: ByteArray): Sound? {
        if (p.size < HEADER || p.size > MAX_PAYLOAD) return null
        val format = u8(p, 0)
        val channels = u8(p, 1)
        val rate = u8(p, 2) or (u8(p, 3) shl 8) or (u8(p, 4) shl 16) or (u8(p, 5) shl 24)
        if (rate !in 8000..192000) return null

        if (format == PCM && (channels == 1 || channels == 2)) {
            val samples = ShortArray((p.size - HEADER) / (channels * 2) * channels)
            for (i in samples.indices) samples[i] = s16(p, HEADER + i * 2)
            return Sound(samples, rate, channels)
        }
        if (format == ADPCM && channels == 2 && p.size >= HEADER + 8) {
            val predicted = IntArray(2)
            val index = IntArray(2)
            for (c in 0..1) {
                predicted[c] = s16(p, HEADER + c * 4).toInt()
                index[c] = minOf(u8(p, HEADER + c * 4 + 2), STEPS.size - 1)
            }
            val start = HEADER + 8
            val samples = ShortArray((p.size - start) * 2)
            for (i in 0 until p.size - start) {
                val both = u8(p, start + i)
                for (c in 0..1) {
                    val code = if (c == 0) both and 15 else both shr 4
                    val step = STEPS[index[c]]
                    var change = step shr 3
                    if ((code and 4) != 0) change += step
                    if ((code and 2) != 0) change += step shr 1
                    if ((code and 1) != 0) change += step shr 2
                    predicted[c] = (predicted[c] + (if ((code and 8) != 0) -change else change)).coerceIn(-32768, 32767)
                    index[c] = (index[c] + INDEX_STEP[code and 7]).coerceIn(0, STEPS.size - 1)
                    samples[i * 2 + c] = predicted[c].toShort()
                }
            }
            return Sound(samples, rate, channels)
        }
        return null
    }
}
