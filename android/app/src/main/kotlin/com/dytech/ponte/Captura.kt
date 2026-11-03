package com.dytech.ponte

import android.content.Context
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.os.Handler
import android.os.HandlerThread
import android.util.DisplayMetrics
import android.view.Display
import android.view.WindowManager
import java.io.ByteArrayOutputStream
import ponte.core.FrameBuilder
import ponte.core.TileEncoder

/** Tamanho da imagem enviada: a tela inteira, reduzida para o lado maior caber em [MAX_SIDE] pixels. */
internal fun scaledSize(width: Int, height: Int, maxSide: Int = Captura.MAX_SIDE): Pair<Int, Int> {
    val longest = maxOf(width, height)
    if (longest <= maxSide) return (width and 1.inv()).coerceAtLeast(2) to (height and 1.inv()).coerceAtLeast(2)
    val scale = maxSide.toDouble() / longest
    return ((width * scale).toInt() and 1.inv()).coerceAtLeast(2) to ((height * scale).toInt() and 1.inv()).coerceAtLeast(2)
}

/**
 * A imagem da tela durante uma sessão. Pela gravação de tela do Android (MediaProjection), fluida, depois que
 * alguém toca em "Começar" no celular; até lá (ou sem isso), pela foto da Acessibilidade, umas 3 por segundo.
 */
class Captura(private val context: Context) {
    private val thread = HandlerThread("ponte-captura").apply { start() }
    private val handler = Handler(thread.looper)
    private val builder = FrameBuilder()
    private val lock = Any()

    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var readerWidth = 0
    private var readerHeight = 0
    private var frameBitmap: Bitmap? = null
    private var last: Bitmap? = null
    private var pixels = IntArray(0)
    private var lastScreenshot = 0L
    @Volatile private var closed = false

    /** A gravação de tela está mandando a imagem (e não a reserva). */
    val projecting: Boolean get() = synchronized(lock) { display != null }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) {
            if (displayId == Display.DEFAULT_DISPLAY) handler.post { resizeIfRotated() }
        }
    }

    init {
        (context.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).registerDisplayListener(displayListener, handler)
    }

    /** Tamanho real da tela agora, já girado. */
    private fun realSize(): Pair<Int, Int> {
        val window = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        return if (android.os.Build.VERSION.SDK_INT >= 30) {
            val bounds = window.currentWindowMetrics.bounds
            bounds.width() to bounds.height()
        } else {
            val metrics = DisplayMetrics()
            @Suppress("DEPRECATION")
            window.defaultDisplay.getRealMetrics(metrics)
            metrics.widthPixels to metrics.heightPixels
        }
    }

    /** Largura e altura da imagem que vai para quem acessa. */
    fun size(): Pair<Int, Int> = realSize().let { scaledSize(it.first, it.second) }

    /** Começa a gravação de tela com a autorização recém-dada. */
    fun start(granted: MediaProjection) = synchronized(lock) {
        if (closed) { granted.stop(); return }
        projection?.stop()
        projection = granted
        granted.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                synchronized(lock) {
                    if (projection !== granted) return
                    projection = null
                    display?.release()
                    display = null
                    reader?.close()
                    reader = null
                }
            }
        }, handler)
        val (width, height) = size()
        val newReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2)
        reader = newReader
        readerWidth = width
        readerHeight = height
        display = granted.createVirtualDisplay(
            "Ponte", width, height, context.resources.displayMetrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, newReader.surface, null, handler,
        )
        builder.reset()
    }

    // O celular girou: a gravação passa a ter o tamanho novo (o Android não gira sozinho a tela virtual).
    private fun resizeIfRotated() = synchronized(lock) {
        val current = display ?: return
        val (width, height) = size()
        if (width == readerWidth && height == readerHeight) return
        val newReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2)
        current.resize(width, height, context.resources.displayMetrics.densityDpi)
        current.surface = newReader.surface
        reader?.close()
        reader = newReader
        readerWidth = width
        readerHeight = height
    }

    /**
     * O próximo quadro (mensagem Frame) ou null quando nada mudou ou ainda não há imagem.
     * [fallback] é a foto pela Acessibilidade, usada enquanto a gravação de tela não começou.
     */
    fun frame(quality: Int, full: Boolean, fallback: ControleService?): ByteArray? {
        var bitmap = synchronized(lock) { if (display != null) fromReader() else null }
            ?: if (!projecting && fallback != null) fromScreenshot(fallback) else null
        if (full) {
            builder.reset()
            // Tela parada não gera imagem nova: a inteira pedida sai da última.
            if (bitmap == null) bitmap = last
        }
        bitmap ?: return null
        if (bitmap !== last && last !== frameBitmap) last?.recycle()
        last = bitmap
        val width = bitmap.width
        val height = bitmap.height
        if (pixels.size != width * height) pixels = IntArray(width * height)
        bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
        val source = pixels
        val encoder = TileEncoder { x, y, w, h, q ->
            val tile = Bitmap.createBitmap(source, y * width + x, width, w, h, Bitmap.Config.ARGB_8888)
            val out = ByteArrayOutputStream(w * h / 4)
            tile.compress(Bitmap.CompressFormat.JPEG, q, out)
            tile.recycle()
            out.toByteArray()
        }
        return builder.build(source, width, height, quality, false, encoder)
    }

    // A imagem mais nova da gravação, copiada (o ImageReader só guarda duas).
    private fun fromReader(): Bitmap? {
        val image = try { reader?.acquireLatestImage() } catch (e: IllegalStateException) { null } ?: return null
        try {
            val plane = image.planes[0]
            val rowPixels = plane.rowStride / plane.pixelStride
            var target = frameBitmap
            if (target == null || target.width != rowPixels || target.height != image.height) {
                if (last === target) last = null
                target?.recycle()
                target = Bitmap.createBitmap(rowPixels, image.height, Bitmap.Config.ARGB_8888)
                frameBitmap = target
            }
            target.copyPixelsFromBuffer(plane.buffer.rewind())
            // A linha da gravação pode ter sobra no fim: recorta a largura de verdade.
            return if (rowPixels == image.width) target else Bitmap.createBitmap(target, 0, 0, image.width, image.height)
        } finally {
            image.close()
        }
    }

    private fun fromScreenshot(service: ControleService): Bitmap? {
        val now = System.currentTimeMillis()
        if (now - lastScreenshot < SCREENSHOT_INTERVAL_MS) return null
        lastScreenshot = now
        val shot = service.screenshot() ?: return null
        val (width, height) = scaledSize(shot.width, shot.height)
        if (width == shot.width && height == shot.height) return shot
        val scaled = Bitmap.createScaledBitmap(shot, width, height, true)
        shot.recycle()
        return scaled
    }

    fun close() {
        closed = true
        (context.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(displayListener)
        synchronized(lock) {
            display?.release()
            display = null
            reader?.close()
            reader = null
            projection?.stop()
            projection = null
        }
        thread.quitSafely()
    }

    companion object {
        /** O lado maior da imagem enviada: nítida para ler, sem pesar na rede do celular. */
        const val MAX_SIDE = 1600
        /** A foto pela Acessibilidade tem limite de frequência no Android. */
        private const val SCREENSHOT_INTERVAL_MS = 350L
    }
}
