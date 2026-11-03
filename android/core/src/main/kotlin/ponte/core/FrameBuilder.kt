package ponte.core

import java.io.ByteArrayOutputStream

/** Codifica em JPEG o retângulo (x, y, largura, altura) da imagem atual. */
fun interface TileEncoder {
    fun encode(x: Int, y: Int, width: Int, height: Int, quality: Int): ByteArray
}

/**
 * Monta a mensagem Frame (tipo 10) comparando a imagem nova com a anterior: só vão as áreas que mudaram, em
 * blocos de [tile] pixels, e blocos vizinhos da mesma faixa que mudaram juntos viram um retângulo só.
 * O formato é o de protocolo.md, seção 6.1, que todos os visualizadores do Ponte já entendem.
 */
class FrameBuilder(private val tile: Int = 128) {
    private var previous: IntArray? = null
    private var previousWidth = 0
    private var previousHeight = 0

    /** Esquece a imagem anterior: o próximo quadro vai inteiro. */
    fun reset() {
        previous = null
    }

    /**
     * @param pixels a imagem atual, linha a linha, com [width] pixels por linha
     * @return a mensagem pronta, ou null se nada mudou
     */
    fun build(pixels: IntArray, width: Int, height: Int, quality: Int, full: Boolean, encoder: TileEncoder): ByteArray? {
        require(width in 1..65535 && height in 1..65535 && pixels.size >= width * height)
        val old = previous
        val everything = full || old == null || previousWidth != width || previousHeight != height
        val rects = ArrayList<IntArray>()
        var y = 0
        while (y < height) {
            val h = minOf(tile, height - y)
            var x = 0
            var runStart = -1
            while (x < width) {
                val w = minOf(tile, width - x)
                val changed = everything || differs(pixels, old!!, width, x, y, w, h)
                if (changed && runStart < 0) runStart = x
                if (!changed && runStart >= 0) {
                    rects.add(intArrayOf(runStart, y, x - runStart, h))
                    runStart = -1
                }
                x += w
            }
            if (runStart >= 0) rects.add(intArrayOf(runStart, y, width - runStart, h))
            y += h
        }
        if (rects.isEmpty()) return null

        val out = ByteArrayOutputStream(64 * 1024)
        fun u16(v: Int) { out.write(v and 0xFF); out.write((v shr 8) and 0xFF) }
        u16(width)
        u16(height)
        u16(rects.size)
        for (r in rects) {
            val jpeg = encoder.encode(r[0], r[1], r[2], r[3], quality)
            u16(r[0]); u16(r[1]); u16(r[2]); u16(r[3])
            u16(jpeg.size and 0xFFFF); u16(jpeg.size ushr 16)
            out.write(jpeg)
        }

        val keep = if (old != null && old.size == width * height) old else IntArray(width * height)
        System.arraycopy(pixels, 0, keep, 0, width * height)
        previous = keep
        previousWidth = width
        previousHeight = height
        return out.toByteArray()
    }

    private fun differs(now: IntArray, before: IntArray, width: Int, x: Int, y: Int, w: Int, h: Int): Boolean {
        for (row in y until y + h) {
            val start = row * width + x
            for (i in start until start + w) if (now[i] != before[i]) return true
        }
        return false
    }
}
