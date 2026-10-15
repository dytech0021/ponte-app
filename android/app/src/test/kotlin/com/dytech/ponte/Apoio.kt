package com.dytech.ponte

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Looper
import android.view.MotionEvent
import android.view.View
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.time.Duration
import org.robolectric.Shadows.shadowOf
import ponte.core.EcIdentity
import ponte.core.Frame
import ponte.core.Identity
import ponte.core.Tile

/** Registra o que a tela e o teclado mandariam ao computador. */
class Recorder(override var acceptsText: Boolean = true) : RemoteInput {
    val events = ArrayList<String>()

    override fun mouse(x: Int, y: Int, action: Int, wheel: Int) {
        events.add("mouse $action $x $y" + if (wheel != 0) " roda $wheel" else "")
    }

    override fun key(virtualKey: Int, up: Boolean, extended: Boolean) {
        events.add("tecla 0x%02X %s%s".format(virtualKey, if (up) "solta" else "aperta", if (extended) " ext" else ""))
    }

    override fun text(text: String) {
        events.add("texto $text")
    }

    /** As ações de mouse registradas, sem as coordenadas. */
    fun actions(): List<Int> = events.filter { it.startsWith("mouse") }.map { it.split(' ')[1].toInt() }

    fun take(): List<String> = events.toList().also { events.clear() }
}

object Apoio {
    /** Fora de um aparelho não há cofre de chaves; os testes usam uma chave em memória. */
    fun identidade(): Identity {
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        return EcIdentity(pair.private, pair.public)
    }

    /** Um quadro com a tela inteira numa cor só, do jeito que o computador manda. */
    fun quadro(width: Int, height: Int, color: Int): Frame {
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888).apply { eraseColor(color) }
        val jpeg = ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG, 80, it) }.toByteArray()
        return Frame(width, height, listOf(Tile(0, 0, width, height, jpeg, 0, jpeg.size)))
    }

    fun desenha(view: View): Bitmap {
        val bitmap = Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888)
        view.draw(Canvas(bitmap))
        return bitmap
    }

    /** Grava em PNG o que a tela está mostrando, para conferir com os olhos. */
    fun captura(activity: Activity, name: String): File {
        val dir = File(System.getProperty("ponte.capturas") ?: "build/capturas").apply { mkdirs() }
        val file = File(dir, name)
        file.outputStream().use { desenha(activity.window.decorView).compress(Bitmap.CompressFormat.PNG, 100, it) }
        println("captura gravada: ${file.absolutePath}")
        return file
    }

    fun avanca(ms: Long) = shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(ms))

    /** Os textos visíveis dentro de uma vista (rótulos e botões). */
    fun textos(view: View): List<String> = when {
        view.visibility != View.VISIBLE -> emptyList()
        view is android.view.ViewGroup -> (0 until view.childCount).flatMap { textos(view.getChildAt(it)) }
        view is android.widget.TextView -> listOf(view.text.toString())
        else -> emptyList()
    }

    /** Toca no rótulo ou botão com este texto, dentro da vista. */
    fun clica(view: View, text: String) {
        fun find(v: View): View? = when {
            v is android.widget.TextView && v.text.toString() == text -> v
            v is android.view.ViewGroup -> (0 until v.childCount).firstNotNullOfOrNull { find(v.getChildAt(it)) }
            else -> null
        }
        (find(view) ?: error("não achei \"$text\"")).performClick()
        shadowOf(Looper.getMainLooper()).idle()
    }

    /** Roda o que estiver pendente na interface até a condição valer, ou o prazo acabar. */
    fun espera(timeoutMs: Long, condition: () -> Boolean): Boolean {
        val end = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < end) {
            shadowOf(Looper.getMainLooper()).idle()
            if (condition()) return true
            Thread.sleep(40)
        }
        shadowOf(Looper.getMainLooper()).idle()
        return condition()
    }

    // ---------- toques ----------

    fun toque(view: View, action: Int, x: Float, y: Float, downTime: Long) {
        val event = MotionEvent.obtain(downTime, android.os.SystemClock.uptimeMillis(), action, x, y, 0)
        view.dispatchTouchEvent(event)
        event.recycle()
    }

    /** Evento com dois dedos. [action] é ACTION_POINTER_DOWN, ACTION_MOVE ou ACTION_POINTER_UP. */
    fun doisDedos(view: View, action: Int, x0: Float, y0: Float, x1: Float, y1: Float, downTime: Long) {
        val properties = Array(2) { MotionEvent.PointerProperties().apply { id = it; toolType = MotionEvent.TOOL_TYPE_FINGER } }
        val coords = arrayOf(
            MotionEvent.PointerCoords().apply { x = x0; y = y0; pressure = 1f; size = 1f },
            MotionEvent.PointerCoords().apply { x = x1; y = y1; pressure = 1f; size = 1f },
        )
        val masked = if (action == MotionEvent.ACTION_MOVE) action else action or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT)
        val event = MotionEvent.obtain(downTime, android.os.SystemClock.uptimeMillis(), masked, 2, properties, coords, 0, 0, 1f, 1f, 0, 0, 0, 0)
        view.dispatchTouchEvent(event)
        event.recycle()
    }
}
