package com.dytech.ponte

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.os.SystemClock
import android.text.InputType
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import ponte.core.Frame
import ponte.core.MouseAction
import ponte.core.PointerShape

/**
 * Mostra a tela do computador e transforma os toques em mouse e o teclado do celular em texto.
 *
 * Dois modos de toque:
 *  - Toque direto: o toque clica onde o dedo encostou; arrastar move a imagem quando ela está ampliada.
 *  - Touchpad: arrastar move um ponteiro, e o toque clica onde o ponteiro está. O ponteiro tem a forma
 *    que tem no computador (seta, barra de texto, mão), que chega pela sessão.
 * Nos dois: segurar até vibrar e arrastar (ou tocar duas vezes e arrastar) segura o botão esquerdo, para
 * arrastar janelas e selecionar texto; segurar até vibrar e soltar sem arrastar é o botão direito; dois
 * dedos rolam a página e a pinça amplia.
 */
class RemoteView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {
    var output: RemoteInput? = null
    var keyboard: Keyboard? = null

    /** Modo touchpad ligado; desligado é o toque direto. */
    var touchpad = false
        set(value) {
            field = value
            invalidate()
        }

    private val density = resources.displayMetrics.density
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private val doubleTapSlop = ViewConfiguration.get(context).scaledDoubleTapSlop.toFloat()
    private val longPressMs = ViewConfiguration.getLongPressTimeout().toLong()
    private val doubleTapMs = ViewConfiguration.getDoubleTapTimeout().toLong()

    // A imagem é escrita pela thread que recebe os quadros e lida pela que desenha.
    private val imageLock = Any()
    private var image: Bitmap? = null
    private var imageWidth = 0
    private var imageHeight = 0

    private val matrix = Matrix()
    private val imagePaint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val pointerFill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
    private val pointerEdge = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK
        style = Paint.Style.STROKE
        strokeJoin = Paint.Join.ROUND
        // Na unidade do desenho da seta, que é ampliado junto com ela: um contorno fino, como o do Windows.
        strokeWidth = 1f
    }
    private val pointerPaint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val pointerMatrix = Matrix()

    // O ponteiro como ele é no computador. Antes da primeira mensagem vale a seta local; uma mensagem oculta suprime o desenho.
    private var remotePointer: Bitmap? = null
    private var pointerHidden = false
    // No toque direto o ponteiro só é desenhado quando se sabe onde ele está: pela posição que o computador
    // manda (Ponte 1.5.1 em diante) ou pelo último toque. Fora da tela mostrada (outro monitor), não aparece.
    private var pointerKnown = false
    private var pointerOnScreen = true
    private var lastLocalMove = 0L
    private var pointerHotX = 0f
    private var pointerHotY = 0f
    private val tapPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2f * density
    }
    private val pointerShape = Path().apply {
        // Seta de mouse, com a ponta na origem.
        moveTo(0f, 0f); lineTo(0f, 17f); lineTo(4.5f, 13f); lineTo(7.5f, 19.5f)
        lineTo(10f, 18.3f); lineTo(7f, 12f); lineTo(12.5f, 12f); close()
    }

    // Escala em pixels do aparelho por pixel do computador, e o deslocamento da imagem na tela.
    private var scale = 0f
    private var offsetX = 0f
    private var offsetY = 0f

    // Posição do ponteiro no computador, em pixels de lá. No toque direto, é o último ponto tocado.
    private var pointerX = 0f
    private var pointerY = 0f

    // Estado do gesto em andamento.
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var moved = false
    private var multiTouch = false
    private var twoFingerMoved = false
    private var longPressed = false
    private var dragging = false
    private var secondTap = false
    private var lastTapTime = 0L
    private var lastTapX = 0f
    private var lastTapY = 0f
    private var lastMoveSent = 0L
    private var twoMode = TWO_UNDECIDED
    private var startSpan = 0f
    private var lastSpan = 0f
    private var startFocusX = 0f
    private var startFocusY = 0f
    private var lastFocusX = 0f
    private var lastFocusY = 0f
    private var wheelRemainder = 0f
    private var tapMarkTime = 0L
    private var tapMarkX = 0f
    private var tapMarkY = 0f

    // Segurar parado "pega" o ponto: a vibração avisa e o ponteiro de lá vai para ele. O que acontece
    // depois depende do dedo: se ele arrastar, o botão esquerdo fica apertado e o que estiver embaixo vai
    // junto (é assim que se arrasta uma janela); se ele sair sem arrastar, vira o botão direito.
    private val longPress = Runnable {
        if (!moved && !multiTouch && !dragging) {
            longPressed = true
            performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            if (!touchpad) movePointerTo(downX, downY)
            send(MouseAction.MOVE)
            invalidate()
        }
    }

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    // ---------- imagem ----------

    /** Aplica um quadro recebido. Pode ser chamado de qualquer thread. */
    fun applyFrame(frame: Frame) {
        // Decodificar é a parte demorada e fica fora da trava, para não segurar o desenho.
        val pieces = frame.tiles.map { BitmapFactory.decodeByteArray(it.data, it.offset, it.length) }
        synchronized(imageLock) {
            var target = image
            if (target == null || target.width != frame.width || target.height != frame.height) {
                target?.recycle()
                target = Bitmap.createBitmap(frame.width, frame.height, Bitmap.Config.ARGB_8888)
                image = target
                imageWidth = frame.width
                imageHeight = frame.height
                scale = 0f
            }
            val canvas = Canvas(target)
            frame.tiles.forEachIndexed { i, tile ->
                pieces[i]?.let { canvas.drawBitmap(it, tile.x.toFloat(), tile.y.toFloat(), null) }
            }
        }
        pieces.forEach { it?.recycle() }
        postInvalidateOnAnimation()
    }

    /** A forma que o ponteiro do mouse tem no computador; null oculta o ponteiro. Pode ser chamado de qualquer thread. */
    fun setPointer(shape: PointerShape?) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        shape?.let { BitmapFactory.decodeByteArray(it.data, it.offset, it.length, bounds) }
        val raw = shape?.takeIf { bounds.outWidth in 1..MAX_POINTER_SIZE && bounds.outHeight in 1..MAX_POINTER_SIZE }
            ?.let { BitmapFactory.decodeByteArray(it.data, it.offset, it.length) }
        val decoded = if (raw != null && (raw.width > MAX_POINTER_SIZE || raw.height > MAX_POINTER_SIZE)) {
            raw.recycle()
            null
        } else {
            raw
        }
        val hotX = if (decoded == null || shape == null) 0f else min(shape.hotX, decoded.width - 1).toFloat()
        val hotY = if (decoded == null || shape == null) 0f else min(shape.hotY, decoded.height - 1).toFloat()
        post {
            pointerHidden = shape == null
            remotePointer?.recycle()
            remotePointer = decoded
            pointerHotX = hotX
            pointerHotY = hotY
            invalidate()
        }
    }

    /** Uma redução da tela do computador, para a lista da tela inicial; null se ainda não há imagem. */
    fun thumbnail(width: Int, height: Int): Bitmap? {
        synchronized(imageLock) {
            val current = image ?: return null
            val fit = min(width / current.width.toFloat(), height / current.height.toFloat())
            val place = Matrix().apply {
                setScale(fit, fit)
                postTranslate((width - current.width * fit) / 2f, (height - current.height * fit) / 2f)
            }
            val result = Bitmap.createBitmap(width, height, Bitmap.Config.RGB_565)
            Canvas(result).apply {
                drawColor(Color.rgb(30, 30, 30))
                drawBitmap(current, place, imagePaint)
            }
            return result
        }
    }

    /**
     * Tela cheia: a imagem cobre o aparelho inteiro, sem faixas, e o que sobra para os lados (ou para
     * cima e para baixo) se alcança arrastando, como com a imagem ampliada. Desligado, a tela do
     * computador aparece inteira, com faixas onde a proporção não bate.
     */
    var fill = false
        set(value) {
            if (field == value) return
            field = value
            scale = 0f
            invalidate()
        }

    /** Onde o ponteiro está no computador, de 0 a 65535 na tela mostrada. Pode ser chamado de qualquer thread. */
    fun setPointerPosition(x: Int, y: Int, onScreen: Boolean) {
        post {
            pointerOnScreen = onScreen
            // No touchpad quem move o ponteiro é o dedo; logo depois de um movimento, a posição que volta de lá
            // é a mesma, só que atrasada, e puxaria o desenho para trás.
            val local = touchpad && SystemClock.uptimeMillis() - lastLocalMove < LOCAL_HOLD_MS
            if (onScreen && !local && imageWidth > 1 && imageHeight > 1) {
                pointerX = x * (imageWidth - 1) / 65535f
                pointerY = y * (imageHeight - 1) / 65535f
                pointerKnown = true
            }
            invalidate()
        }
    }

    /** Volta a mostrar a tela inteira do computador (ou a cobrir o aparelho, na tela cheia). */
    fun fitToScreen() {
        scale = 0f
        invalidate()
    }

    private fun minScale(): Float = when {
        imageWidth == 0 || width == 0 -> 1f
        fill -> max(width / imageWidth.toFloat(), height / imageHeight.toFloat())
        else -> min(width / imageWidth.toFloat(), height / imageHeight.toFloat())
    }

    // Mantém a escala e o deslocamento dentro do que faz sentido: nunca menor que a tela inteira,
    // centralizada quando sobra espaço, sem deixar faixa vazia quando não sobra.
    private fun settle() {
        if (imageWidth == 0 || width == 0 || height == 0) return
        val least = minScale()
        if (scale <= 0f) {
            // Escala nova (primeira imagem, tela cheia ligada ou desligada): a imagem recomeça centralizada.
            scale = least
            offsetX = (width - imageWidth * scale) / 2f
            offsetY = (height - imageHeight * scale) / 2f
            if (!pointerKnown) {
                pointerX = imageWidth / 2f
                pointerY = imageHeight / 2f
            }
        }
        scale = scale.coerceIn(least, max(least * 8f, 3f))
        val shownWidth = imageWidth * scale
        val shownHeight = imageHeight * scale
        offsetX = if (shownWidth <= width) (width - shownWidth) / 2f else offsetX.coerceIn(width - shownWidth, 0f)
        offsetY = if (shownHeight <= height) (height - shownHeight) / 2f else offsetY.coerceIn(height - shownHeight, 0f)
    }

    // Traz o ponteiro (ou o último ponto tocado) para dentro da área visível; usado no touchpad e quando
    // o teclado abre e encolhe a tela.
    private fun keepPointerVisible() {
        if (imageWidth == 0) return
        val margin = 24f * density
        val x = pointerX * scale + offsetX
        val y = pointerY * scale + offsetY
        if (x < margin) offsetX += margin - x else if (x > width - margin) offsetX -= x - (width - margin)
        if (y < margin) offsetY += margin - y else if (y > height - margin) offsetY -= y - (height - margin)
        settle()
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        settle()
        keepPointerVisible()
    }

    override fun onDraw(canvas: Canvas) {
        synchronized(imageLock) {
            val current = image
            if (current == null) return
            settle()
            matrix.setScale(scale, scale)
            matrix.postTranslate(offsetX, offsetY)
            canvas.drawBitmap(current, matrix, imagePaint)
        }
        if (!pointerHidden && pointerOnScreen && (touchpad || pointerKnown)) {
            val x = pointerX * scale + offsetX
            val y = pointerY * scale + offsetY
            val shape = remotePointer
            if (shape != null) {
                // Do tamanho que tem lá quando a imagem está ampliada, mas nunca pequeno demais para ver no celular.
                val size = max(scale, POINTER_MIN_DP * density / shape.height)
                pointerMatrix.setScale(size, size)
                pointerMatrix.postTranslate(x - pointerHotX * size, y - pointerHotY * size)
                canvas.drawBitmap(shape, pointerMatrix, pointerPaint)
            } else {
                canvas.save()
                canvas.translate(x, y)
                canvas.scale(density * 1.2f, density * 1.2f)
                canvas.drawPath(pointerShape, pointerFill)
                canvas.drawPath(pointerShape, pointerEdge)
                canvas.restore()
            }
        }
        // Marca rápida onde o toque virou clique, já que no toque direto não há ponteiro para ver.
        val age = SystemClock.uptimeMillis() - tapMarkTime
        if (tapMarkTime > 0 && age < TAP_MARK_MS) {
            val progress = age / TAP_MARK_MS.toFloat()
            tapPaint.color = Color.argb((200 * (1f - progress)).toInt(), 79, 195, 212)
            canvas.drawCircle(tapMarkX, tapMarkY, (10f + 18f * progress) * density, tapPaint)
            postInvalidateOnAnimation()
        }
    }

    // ---------- mouse ----------

    private fun toRemoteX(viewX: Float) = ((viewX - offsetX) / scale).coerceIn(0f, max(0f, imageWidth - 1f))
    private fun toRemoteY(viewY: Float) = ((viewY - offsetY) / scale).coerceIn(0f, max(0f, imageHeight - 1f))

    private fun movePointerTo(viewX: Float, viewY: Float) {
        if (scale <= 0f) return
        pointerX = toRemoteX(viewX)
        pointerY = toRemoteY(viewY)
    }

    private fun send(action: Int, wheel: Int = 0) {
        if (imageWidth < 2 || imageHeight < 2) return
        lastLocalMove = SystemClock.uptimeMillis()
        pointerKnown = true
        pointerOnScreen = true
        val nx = (pointerX * 65535f / (imageWidth - 1)).roundToInt()
        val ny = (pointerY * 65535f / (imageHeight - 1)).roundToInt()
        output?.mouse(nx, ny, action, wheel)
    }

    // Um movimento a cada 12 ms basta; mais que isso só enche a fila de envio.
    private fun sendMove() {
        val now = SystemClock.uptimeMillis()
        if (now - lastMoveSent < 12) return
        lastMoveSent = now
        send(MouseAction.MOVE)
    }

    private fun click(down: Int, up: Int) {
        val act = {
            send(MouseAction.MOVE)
            send(down)
            send(up)
        }
        // Com Ctrl ou Shift armado na barra de teclas, o clique sai com a tecla segurada.
        keyboard?.withArmed(act) ?: act()
        tapMarkTime = SystemClock.uptimeMillis()
        tapMarkX = pointerX * scale + offsetX
        tapMarkY = pointerY * scale + offsetY
        invalidate()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (image == null || scale <= 0f) return true
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                lastX = event.x
                lastY = event.y
                moved = false
                multiTouch = false
                twoFingerMoved = false
                longPressed = false
                val now = SystemClock.uptimeMillis()
                secondTap = now - lastTapTime < doubleTapMs && hypot(downX - lastTapX, downY - lastTapY) < doubleTapSlop
                if (!secondTap) postDelayed(longPress, longPressMs)
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                removeCallbacks(longPress)
                multiTouch = true
                endDrag()
                if (event.pointerCount == 2) {
                    twoMode = TWO_UNDECIDED
                    startSpan = span(event)
                    lastSpan = startSpan
                    startFocusX = focusX(event)
                    startFocusY = focusY(event)
                    lastFocusX = startFocusX
                    lastFocusY = startFocusY
                    wheelRemainder = 0f
                }
            }

            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount >= 2) twoFingers(event) else if (!multiTouch) oneFinger(event)
            }

            MotionEvent.ACTION_UP -> {
                removeCallbacks(longPress)
                if (dragging) {
                    endDrag()
                } else if (multiTouch) {
                    // Dois dedos encostados e soltos sem mover: botão direito.
                    if (!twoFingerMoved && event.eventTime - event.downTime < 400) {
                        if (!touchpad) movePointerTo(startFocusX, startFocusY)
                        click(MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP)
                    }
                } else if (longPressed && !moved) {
                    // Segurou até vibrar e soltou sem arrastar: botão direito, no ponto pego.
                    click(MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP)
                } else if (!moved && !longPressed) {
                    // No toque direto, o segundo toque de um duplo clique reaproveita o ponto do primeiro:
                    // o dedo nunca cai exatamente no mesmo lugar, e o Windows exige que caia.
                    if (!touchpad && !secondTap) movePointerTo(downX, downY)
                    click(MouseAction.LEFT_DOWN, MouseAction.LEFT_UP)
                    lastTapTime = SystemClock.uptimeMillis()
                    lastTapX = downX
                    lastTapY = downY
                }
            }

            MotionEvent.ACTION_CANCEL -> {
                removeCallbacks(longPress)
                endDrag()
            }
        }
        return true
    }

    private fun oneFinger(event: MotionEvent) {
        val dx = event.x - lastX
        val dy = event.y - lastY
        if (!moved) {
            if (hypot(event.x - downX, event.y - downY) < touchSlop) return
            moved = true
            removeCallbacks(longPress)
            // Segurar até vibrar e arrastar, ou tocar duas vezes e arrastar: segura o botão esquerdo no
            // ponto pego, para arrastar janelas e selecionar texto.
            if (longPressed || secondTap) {
                dragging = true
                // Logo depois de um toque, o Windows entende outro "apertar" no mesmo ponto como clique duplo,
                // e clique duplo na barra de título maximiza a janela. No "tocar duas vezes e arrastar", o botão
                // desce um pouco adiante, na direção em que o dedo começou a andar: fora da área do clique duplo
                // e ainda sobre o que se quer arrastar.
                if (secondTap) nudgePointer(event.x - downX, event.y - downY)
                send(MouseAction.MOVE)
                send(MouseAction.LEFT_DOWN)
            }
        }
        lastX = event.x
        lastY = event.y
        when {
            touchpad -> {
                pointerX = (pointerX + dx / scale * POINTER_SPEED).coerceIn(0f, imageWidth - 1f)
                pointerY = (pointerY + dy / scale * POINTER_SPEED).coerceIn(0f, imageHeight - 1f)
                keepPointerVisible()
                sendMove()
            }
            dragging -> {
                movePointerTo(event.x, event.y)
                sendMove()
            }
            else -> {
                offsetX += dx
                offsetY += dy
                settle()
            }
        }
        invalidate()
    }

    private fun twoFingers(event: MotionEvent) {
        val nowSpan = span(event)
        val fx = focusX(event)
        val fy = focusY(event)
        if (twoMode == TWO_UNDECIDED) {
            // A pinça muda a distância entre os dedos; a rolagem move os dois juntos.
            if (abs(nowSpan - startSpan) > 28f * density) twoMode = TWO_ZOOM
            else if (hypot(fx - startFocusX, fy - startFocusY) > 14f * density) twoMode = TWO_SCROLL
            if (twoMode != TWO_UNDECIDED) {
                twoFingerMoved = true
                lastSpan = nowSpan
                lastFocusX = fx
                lastFocusY = fy
            }
            return
        }
        if (twoMode == TWO_ZOOM) {
            val before = scale
            scale = (scale * (nowSpan / max(1f, lastSpan))).coerceIn(minScale(), max(minScale() * 8f, 3f))
            // O ponto entre os dedos continua sob os dedos.
            offsetX = fx - (lastFocusX - offsetX) * (scale / before)
            offsetY = fy - (lastFocusY - offsetY) * (scale / before)
            settle()
        } else {
            // Rolagem natural, como na tela do celular: o conteúdo acompanha os dedos.
            wheelRemainder += (fy - lastFocusY) / (WHEEL_STEP_DP * density)
            val steps = wheelRemainder.toInt()
            if (steps != 0) {
                wheelRemainder -= steps
                if (!touchpad) movePointerTo(fx, fy)
                send(MouseAction.WHEEL, steps * 120)
            }
        }
        lastSpan = nowSpan
        lastFocusX = fx
        lastFocusY = fy
        invalidate()
    }

    // Move o ponteiro DOUBLE_CLICK_ESCAPE pixels do computador na direção dada. O Windows só junta dois
    // cliques num clique duplo dentro de um quadradinho em volta do primeiro (4 px, mais em tela ampliada).
    private fun nudgePointer(dx: Float, dy: Float) {
        val length = hypot(dx, dy)
        if (length <= 0f || imageWidth < 2 || imageHeight < 2) return
        pointerX = (pointerX + dx / length * DOUBLE_CLICK_ESCAPE).coerceIn(0f, imageWidth - 1f)
        pointerY = (pointerY + dy / length * DOUBLE_CLICK_ESCAPE).coerceIn(0f, imageHeight - 1f)
    }

    private fun endDrag() {
        if (!dragging) return
        dragging = false
        send(MouseAction.LEFT_UP)
    }

    private fun span(e: MotionEvent) = hypot(e.getX(0) - e.getX(1), e.getY(0) - e.getY(1))
    private fun focusX(e: MotionEvent) = (e.getX(0) + e.getX(1)) / 2f
    private fun focusY(e: MotionEvent) = (e.getY(0) + e.getY(1)) / 2f

    // ---------- teclado ----------

    fun showKeyboard() {
        requestFocus()
        val input = context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        input.showSoftInput(this, 0)
    }

    fun hideKeyboard() {
        val input = context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        input.hideSoftInputFromWindow(windowToken, 0)
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        // "Senha visível" faz os teclados desligarem a sugestão de palavras e entregarem cada letra na hora.
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_ACTION_NONE
        return TypedInput()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean =
        keyboard?.onKeyEvent(event) == true || super.onKeyDown(keyCode, event)

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean =
        keyboard?.onKeyEvent(event) == true || super.onKeyUp(keyCode, event)

    /** Recebe o que o teclado virtual produz e repassa ao computador. Não há texto guardado deste lado. */
    private inner class TypedInput : BaseInputConnection(this@RemoteView, false) {
        // Alguns teclados insistem em "compor" a palavra antes de confirmá-la; aqui vai só a diferença.
        private var composing = ""

        private fun replaceComposing(now: String) {
            val keys = keyboard ?: return
            var common = composing.commonPrefixWith(now).length
            // Não corta um emoji ao meio: ele ocupa duas posições no texto.
            if (common > 0 && Character.isHighSurrogate(composing[common - 1])) common--
            if (composing.length > common) keys.backspace(composing.codePointCount(common, composing.length))
            if (now.length > common) keys.type(now.substring(common))
            composing = now
        }

        override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
            replaceComposing(text?.toString() ?: "")
            composing = ""
            return true
        }

        override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
            replaceComposing(text?.toString() ?: "")
            return true
        }

        override fun finishComposingText(): Boolean {
            composing = ""
            return true
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            val keys = keyboard ?: return true
            keys.backspace(beforeLength)
            repeat(afterLength) { keys.tap(Vk.DELETE) }
            return true
        }

        override fun sendKeyEvent(event: KeyEvent): Boolean {
            keyboard?.onKeyEvent(event)
            return true
        }

        override fun performEditorAction(actionCode: Int): Boolean {
            keyboard?.tap(Vk.RETURN)
            return true
        }
    }

    private companion object {
        const val TWO_UNDECIDED = 0
        const val TWO_ZOOM = 1
        const val TWO_SCROLL = 2
        const val POINTER_SPEED = 1.3f
        const val DOUBLE_CLICK_ESCAPE = 10f
        const val WHEEL_STEP_DP = 40f
        const val TAP_MARK_MS = 350L
        const val MAX_POINTER_SIZE = 256
        // A imagem de um ponteiro comum tem 32 pontos de altura, e a seta ocupa pouco mais da metade.
        const val POINTER_MIN_DP = 28f
        const val LOCAL_HOLD_MS = 600L
    }
}
