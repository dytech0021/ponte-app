package com.dytech.ponte

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Path
import android.graphics.Point
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.Display
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import java.lang.ref.WeakReference
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import ponte.core.MouseAction
import ponte.core.PhoneAction

/**
 * O controle do celular por quem acessa, pelo serviço de Acessibilidade do Android: é o único jeito que o Android
 * dá a um aplicativo de tocar na tela, apertar Voltar e Início e escrever nos campos de outros aplicativos.
 * A pessoa liga uma vez em Configurações → Acessibilidade → Ponte. O serviço não lê nem guarda nada da tela.
 */
class ControleService : AccessibilityService() {
    private val main = Handler(Looper.getMainLooper())
    private val gestures = Gestures()

    override fun onServiceConnected() {
        super.onServiceConnected()
        current = WeakReference(this)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onDestroy() {
        if (current?.get() === this) current = null
        super.onDestroy()
    }

    // ---------- coordenadas ----------

    /** Tamanho real da tela agora, em pixels, já girado. */
    fun screenSize(): Point {
        val window = getSystemService(WINDOW_SERVICE) as WindowManager
        return if (Build.VERSION.SDK_INT >= 30) {
            val bounds = window.currentWindowMetrics.bounds
            Point(bounds.width(), bounds.height())
        } else {
            Point().also {
                @Suppress("DEPRECATION")
                window.defaultDisplay.getRealSize(it)
            }
        }
    }

    // x e y chegam de 0 a 65535 na imagem mostrada, que é a tela inteira.
    private fun toScreen(x: Int, y: Int): Pair<Float, Float> {
        val size = screenSize()
        return (x / 65535f * (size.x - 1)) to (y / 65535f * (size.y - 1))
    }

    // ---------- mouse e toque ----------

    fun mouse(x: Int, y: Int, action: Int, wheel: Int) {
        val (px, py) = toScreen(x, y)
        main.post {
            when (action) {
                MouseAction.MOVE -> gestures.move(px, py)
                MouseAction.LEFT_DOWN -> gestures.press(px, py)
                MouseAction.LEFT_UP -> gestures.release(px, py)
                MouseAction.RIGHT_DOWN -> performGlobalAction(GLOBAL_ACTION_BACK)
                MouseAction.MIDDLE_DOWN -> performGlobalAction(GLOBAL_ACTION_HOME)
                MouseAction.WHEEL -> gestures.scroll(px, py, wheel)
            }
        }
    }

    /** Solta o dedo, se algum ficou apertado (fim da sessão). */
    fun releaseAll() = main.post { gestures.cancel() }

    /**
     * Toque, toque longo e arrastar como um dedo de verdade: o dedo desce no clique, acompanha o mouse em pedaços
     * contínuos e sobe ao soltar. Um clique rápido sem movimento vira um toque simples, que todo aplicativo entende.
     */
    private inner class Gestures {
        private var down = false
        private var lastX = 0f
        private var lastY = 0f
        private var downX = 0f
        private var downY = 0f
        private var stroke: GestureDescription.StrokeDescription? = null
        private var strokeEndX = 0f
        private var strokeEndY = 0f
        private var lastDispatch = 0L
        private var inFlight = false
        private var releasePending = false
        private var start: Runnable? = null
        private var generation = 0

        fun move(x: Float, y: Float) {
            lastX = x
            lastY = y
            if (!down) return
            if (start != null && (Math.abs(x - downX) > SLOP || Math.abs(y - downY) > SLOP)) {
                main.removeCallbacks(start!!)
                start = null
                begin()
            } else {
                pump()
            }
        }

        fun press(x: Float, y: Float) {
            cancel()
            down = true
            downX = x
            downY = y
            lastX = x
            lastY = y
            // Espera um pouco: se o botão soltar logo, sem mexer, é um toque simples.
            start = Runnable { start = null; begin() }.also { main.postDelayed(it, TAP_WAIT_MS) }
        }

        fun release(x: Float, y: Float) {
            lastX = x
            lastY = y
            if (!down) return
            down = false
            val waiting = start
            if (waiting != null) {
                main.removeCallbacks(waiting)
                start = null
                dispatch(GestureDescription.StrokeDescription(dot(downX, downY), 0, TAP_MS), null)
                return
            }
            releasePending = true
            pump()
        }

        fun scroll(x: Float, y: Float, wheel: Int) {
            if (down || wheel == 0) return
            val size = screenSize()
            // Rodinha para cima = conteúdo desce = o dedo arrasta para baixo.
            val distance = (wheel / 120f) * size.y / 7f
            val fromY = y.coerceIn(size.y * 0.15f, size.y * 0.85f)
            val toY = (fromY + distance).coerceIn(1f, size.y - 2f)
            val path = Path().apply { moveTo(x, fromY); lineTo(x, toY) }
            dispatch(GestureDescription.StrokeDescription(path, 0, 180), null)
        }

        fun cancel() {
            start?.let { main.removeCallbacks(it) }
            start = null
            val holding = stroke
            stroke = null
            down = false
            releasePending = false
            generation++
            inFlight = false
            if (holding != null) {
                try {
                    dispatch(holding.continueStroke(line(strokeEndX, strokeEndY, strokeEndX, strokeEndY + 1), 0, 10, false), null)
                } catch (ignored: RuntimeException) {
                }
            }
        }

        private fun begin() {
            val first = GestureDescription.StrokeDescription(line(downX, downY, lastX, lastY), 0, SEGMENT_MIN_MS, true)
            stroke = first
            strokeEndX = if (lastX == downX && lastY == downY) downX else lastX
            strokeEndY = if (lastX == downX && lastY == downY) downY + 1 else lastY
            send(first)
        }

        private fun pump() {
            val current = stroke ?: return
            if (inFlight) return
            val moved = lastX != strokeEndX || lastY != strokeEndY
            if (!moved && !releasePending) return
            val elapsed = SystemClock.uptimeMillis() - lastDispatch
            val duration = elapsed.coerceIn(SEGMENT_MIN_MS, SEGMENT_MAX_MS)
            val endX = if (moved) lastX else strokeEndX
            val endY = if (moved) lastY else strokeEndY + 1
            val next = try {
                current.continueStroke(line(strokeEndX, strokeEndY, endX, endY), 0, duration, !releasePending)
            } catch (e: RuntimeException) {
                stroke = null
                return
            }
            strokeEndX = endX
            strokeEndY = endY
            if (releasePending) {
                releasePending = false
                stroke = null
            } else {
                stroke = next
            }
            send(next)
        }

        private fun send(next: GestureDescription.StrokeDescription) {
            inFlight = true
            lastDispatch = SystemClock.uptimeMillis()
            val mine = generation
            dispatch(next, object : GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription?) {
                    if (mine != generation) return
                    inFlight = false
                    pump()
                }

                override fun onCancelled(gestureDescription: GestureDescription?) {
                    if (mine != generation) return
                    inFlight = false
                    stroke = null
                    releasePending = false
                }
            })
        }

        private fun dispatch(stroke: GestureDescription.StrokeDescription, callback: GestureResultCallback?) {
            try {
                dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), callback, main)
            } catch (ignored: RuntimeException) {
                callback?.onCancelled(null)
            }
        }

        // O Android não aceita um caminho de comprimento zero: um toque parado anda um pixel.
        private fun dot(x: Float, y: Float) = line(x, y, x, y + 1)

        private fun line(fromX: Float, fromY: Float, toX: Float, toY: Float) = Path().apply {
            moveTo(fromX, fromY)
            if (fromX == toX && fromY == toY) lineTo(toX, toY + 1) else lineTo(toX, toY)
        }
    }

    // ---------- botões e teclado ----------

    fun perform(action: PhoneAction) = main.post {
        when (action) {
            PhoneAction.BACK -> performGlobalAction(GLOBAL_ACTION_BACK)
            PhoneAction.HOME -> performGlobalAction(GLOBAL_ACTION_HOME)
            PhoneAction.RECENTS -> performGlobalAction(GLOBAL_ACTION_RECENTS)
            PhoneAction.NOTIFICATIONS -> performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)
            else -> edit(action)
        }
    }

    /** Escreve no campo em foco. Falso quando não há campo de texto em foco. */
    fun type(text: String): Boolean {
        val done = CountDownLatch(1)
        var ok = false
        main.post {
            ok = replaceSelection(text)
            done.countDown()
        }
        done.await(2, TimeUnit.SECONDS)
        return ok
    }

    private fun focused(): AccessibilityNodeInfo? =
        (rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: findFocus(AccessibilityNodeInfo.FOCUS_INPUT))
            ?.takeIf { it.isEditable }

    private fun content(node: AccessibilityNodeInfo): String =
        if (Build.VERSION.SDK_INT >= 26 && node.isShowingHintText) "" else node.text?.toString() ?: ""

    private fun selection(node: AccessibilityNodeInfo, text: String): IntRange {
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0 || start > text.length || end > text.length) { start = text.length; end = text.length }
        return minOf(start, end)..maxOf(start, end)
    }

    private fun setText(node: AccessibilityNodeInfo, text: String, caret: Int, caretEnd: Int = caret): Boolean {
        val args = Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text) }
        if (!node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) return false
        select(node, caret, caretEnd)
        return true
    }

    private fun select(node: AccessibilityNodeInfo, start: Int, end: Int = start) {
        val args = Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, start)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, end)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, args)
    }

    private fun replaceSelection(inserted: String): Boolean {
        val node = focused() ?: return false
        val text = content(node)
        val range = selection(node, text)
        val updated = text.substring(0, range.first) + inserted + text.substring(range.last)
        return setText(node, updated, range.first + inserted.length)
    }

    private fun edit(action: PhoneAction) {
        val node = focused()
        if (node == null) {
            // Sem campo em foco, Enter e as setas não têm onde agir; Enter vira "OK" do teclado quando dá.
            return
        }
        val text = content(node)
        val range = selection(node, text)
        when (action) {
            PhoneAction.ENTER -> {
                if (node.isMultiLine) replaceSelection("\n")
                else if (Build.VERSION.SDK_INT >= 30) node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
            }
            PhoneAction.TAB -> {}
            PhoneAction.BACKSPACE -> {
                if (range.first != range.last) setText(node, text.removeRange(range.first, range.last), range.first)
                else if (range.first > 0) {
                    val from = if (range.first >= 2 && Character.isSurrogatePair(text[range.first - 2], text[range.first - 1])) range.first - 2 else range.first - 1
                    setText(node, text.removeRange(from, range.first), from)
                }
            }
            PhoneAction.DELETE -> {
                if (range.first != range.last) setText(node, text.removeRange(range.first, range.last), range.first)
                else if (range.first < text.length) {
                    val to = if (range.first + 1 < text.length && Character.isSurrogatePair(text[range.first], text[range.first + 1])) range.first + 2 else range.first + 1
                    setText(node, text.removeRange(range.first, to), range.first)
                }
            }
            PhoneAction.LEFT -> select(node, maxOf(0, range.first - 1))
            PhoneAction.RIGHT -> select(node, minOf(text.length, range.last + 1))
            PhoneAction.LINE_START -> select(node, text.lastIndexOf('\n', range.first - 1) + 1)
            PhoneAction.LINE_END -> select(node, text.indexOf('\n', range.last).let { if (it < 0) text.length else it })
            PhoneAction.UP, PhoneAction.DOWN -> {}
            PhoneAction.SELECT_ALL -> select(node, 0, text.length)
            PhoneAction.COPY -> node.performAction(AccessibilityNodeInfo.ACTION_COPY)
            PhoneAction.CUT -> node.performAction(AccessibilityNodeInfo.ACTION_CUT)
            PhoneAction.PASTE -> node.performAction(AccessibilityNodeInfo.ACTION_PASTE)
            else -> {}
        }
    }

    /** O que vem copiado de quem acessa vai para a área de transferência do celular. */
    fun clipboard(text: String) = main.post {
        try {
            (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("Ponte", text))
        } catch (ignored: RuntimeException) {
        }
    }

    // ---------- imagem de reserva ----------

    /**
     * Uma foto da tela pelo serviço de Acessibilidade (Android 11 em diante). É lenta (umas 3 por segundo), mas não
     * pede autorização a cada sessão: é a imagem enquanto ninguém tocou em "Começar" no celular.
     */
    fun screenshot(): Bitmap? {
        if (Build.VERSION.SDK_INT < 30) return null
        val done = CountDownLatch(1)
        var result: Bitmap? = null
        try {
            takeScreenshot(Display.DEFAULT_DISPLAY, mainExecutor, object : TakeScreenshotCallback {
                override fun onSuccess(screenshot: ScreenshotResult) {
                    try {
                        val buffer = screenshot.hardwareBuffer
                        val wrapped = Bitmap.wrapHardwareBuffer(buffer, screenshot.colorSpace)
                        result = wrapped?.copy(Bitmap.Config.ARGB_8888, false)
                        wrapped?.recycle()
                        buffer.close()
                    } finally {
                        done.countDown()
                    }
                }

                override fun onFailure(errorCode: Int) = done.countDown()
            })
        } catch (e: RuntimeException) {
            return null
        }
        done.await(2, TimeUnit.SECONDS)
        return result
    }

    companion object {
        private const val SLOP = 12f
        private const val TAP_WAIT_MS = 160L
        private const val TAP_MS = 60L
        private const val SEGMENT_MIN_MS = 16L
        private const val SEGMENT_MAX_MS = 120L

        @Volatile private var current: WeakReference<ControleService>? = null

        /** O serviço ligado agora, ou null quando a pessoa ainda não ligou o Ponte em Acessibilidade. */
        val instance: ControleService? get() = current?.get()

        /** O Ponte está ligado em Configurações → Acessibilidade (mesmo que o serviço ainda esteja subindo). */
        fun enabled(context: Context): Boolean {
            val wanted = ComponentName(context, ControleService::class.java).flattenToString()
            val list = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
            return list.split(':').any { it.equals(wanted, ignoreCase = true) }
        }
    }
}
