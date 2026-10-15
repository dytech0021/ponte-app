package com.dytech.ponte

import android.view.KeyCharacterMap
import android.view.KeyEvent

/** Códigos de tecla virtual do Windows, que é o que o protocolo do Ponte carrega. */
object Vk {
    const val BACK = 0x08
    const val TAB = 0x09
    const val RETURN = 0x0D
    const val SHIFT = 0x10
    const val CONTROL = 0x11
    const val ALT = 0x12
    const val CAPITAL = 0x14
    const val ESCAPE = 0x1B
    const val SPACE = 0x20
    const val PAGE_UP = 0x21
    const val PAGE_DOWN = 0x22
    const val END = 0x23
    const val HOME = 0x24
    const val LEFT = 0x25
    const val UP = 0x26
    const val RIGHT = 0x27
    const val DOWN = 0x28
    const val PRINT_SCREEN = 0x2C
    const val INSERT = 0x2D
    const val DELETE = 0x2E
    const val WIN = 0x5B
    const val F1 = 0x70
    const val F4 = 0x73

    /** Teclas que o Windows espera marcadas como "estendidas", para não confundir com o teclado numérico. */
    fun extended(vk: Int) = vk in PAGE_UP..DOWN || vk == INSERT || vk == DELETE || vk == WIN
}

/** Para onde vão o mouse e o teclado: a sessão com o computador ou, nos testes, um registro do que foi enviado. */
interface RemoteInput {
    /** O computador sabe receber texto pronto (Ponte 1.3.0 em diante); antes disso, só teclas. */
    val acceptsText: Boolean

    fun mouse(x: Int, y: Int, action: Int, wheel: Int)
    fun key(virtualKey: Int, up: Boolean, extended: Boolean)
    fun text(text: String)
}

/**
 * Transforma o que vem do teclado do celular em mensagens para o computador.
 *
 * O teclado virtual entrega texto pronto, que segue como texto (o computador digita qualquer
 * caractere, em qualquer layout). Teclas de comando e combinações seguem como teclas. Ctrl, Alt,
 * Shift e Windows podem ficar "armados" pela barra de teclas: valem para a próxima tecla ou clique.
 */
class Keyboard(private val output: () -> RemoteInput?) {
    private val armed = LinkedHashSet<Int>()
    private val sentAsKey = HashSet<Int>()
    private var warnedOldHost = false

    /** Avisa a barra de teclas para redesenhar o que está armado. */
    var onArmedChanged: () -> Unit = {}

    /** Mensagem para mostrar a quem está operando. */
    var onNotice: (String) -> Unit = {}

    fun isArmed(vk: Int) = vk in armed

    fun toggleArmed(vk: Int) {
        if (!armed.remove(vk)) armed.add(vk)
        onArmedChanged()
    }

    private fun down(vk: Int) = output()?.key(vk, false, Vk.extended(vk))
    private fun up(vk: Int) = output()?.key(vk, true, Vk.extended(vk))

    /** Executa uma ação com os modificadores armados pressionados e depois os desarma. */
    fun withArmed(action: () -> Unit) {
        val held = armed.toList()
        held.forEach { down(it) }
        action()
        held.reversed().forEach { up(it) }
        if (held.isNotEmpty()) {
            armed.clear()
            onArmedChanged()
        }
    }

    /** Aperta e solta uma tecla, com o que estiver armado. */
    fun tap(vk: Int) = withArmed {
        down(vk)
        up(vk)
    }

    /** Aperta as teclas na ordem dada e solta na ordem inversa, como em Alt+Tab. */
    fun combo(vararg vks: Int) {
        vks.forEach { down(it) }
        vks.reversed().forEach { up(it) }
    }

    fun backspace(times: Int) = repeat(times) {
        down(Vk.BACK)
        up(Vk.BACK)
    }

    /** Texto vindo do teclado virtual ou da área de transferência. */
    fun type(text: String) {
        if (text.isEmpty()) return
        val current = output() ?: return
        // Com Ctrl, Alt ou Windows armado, uma letra é um atalho (Ctrl+C), não um caractere.
        if (armed.any { it != Vk.SHIFT } && text.length == 1) {
            keyFor(text[0])?.let { vk ->
                tap(vk)
                return
            }
        }
        if (armed.isNotEmpty()) {
            armed.clear()
            onArmedChanged()
        }
        if (current.acceptsText) {
            current.text(text)
            return
        }
        // Computador com Ponte anterior à 1.3.0: só entende teclas, então vai o que tem tecla certa em qualquer layout.
        var skipped = false
        for (c in text) {
            val vk = keyFor(c)
            when {
                vk == null -> skipped = true
                c.isUpperCase() -> combo(Vk.SHIFT, vk)
                else -> combo(vk)
            }
        }
        if (skipped && !warnedOldHost) {
            warnedOldHost = true
            onNotice("O Ponte desse computador é antigo e só recebe letras e números. Atualize-o para digitar acentos e símbolos.")
        }
    }

    private fun keyFor(c: Char): Int? = when (c) {
        in 'a'..'z' -> 0x41 + (c - 'a')
        in 'A'..'Z' -> 0x41 + (c - 'A')
        in '0'..'9' -> 0x30 + (c - '0')
        ' ' -> Vk.SPACE
        '\n' -> Vk.RETURN
        '\t' -> Vk.TAB
        else -> null
    }

    /**
     * Tecla que chega como evento: teclado físico (Bluetooth, USB) ou teclas como Apagar e Enter do
     * teclado virtual. Devolve falso para o que não é deste aplicativo tratar, como o botão Voltar.
     */
    fun onKeyEvent(event: KeyEvent): Boolean {
        val code = event.keyCode
        if (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_VOLUME_UP || code == KeyEvent.KEYCODE_VOLUME_DOWN ||
            code == KeyEvent.KEYCODE_HOME || code == KeyEvent.KEYCODE_POWER) return false

        val down = event.action == KeyEvent.ACTION_DOWN
        val vk = vkFor(code)
        val shortcut = event.isCtrlPressed || event.isAltPressed || event.isMetaPressed
        if (vk != null && (isCommandKey(code) || shortcut)) {
            if (down) {
                sentAsKey.add(vk)
                if (event.repeatCount == 0 && !isModifier(code)) withArmed { down(vk) } else down(vk)
            } else if (sentAsKey.remove(vk)) {
                up(vk)
            }
            return true
        }
        val char = event.unicodeChar
        if (char != 0 && (char and KeyCharacterMap.COMBINING_ACCENT) == 0) {
            if (down) type(String(Character.toChars(char)))
            return true
        }
        return false
    }

    /** Solta o que ficou pressionado por um teclado físico, para a sessão não terminar com uma tecla presa. */
    fun releaseAll() {
        sentAsKey.toList().forEach { up(it) }
        sentAsKey.clear()
    }

    private fun isModifier(code: Int) = code in MODIFIERS

    private fun isCommandKey(code: Int) = code in COMMAND_KEYS || code in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12

    private fun vkFor(code: Int): Int? = when (code) {
        in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z -> 0x41 + (code - KeyEvent.KEYCODE_A)
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> 0x30 + (code - KeyEvent.KEYCODE_0)
        in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 -> Vk.F1 + (code - KeyEvent.KEYCODE_F1)
        else -> COMMAND_KEYS[code]
    }

    private companion object {
        val MODIFIERS = setOf(
            KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_SHIFT_RIGHT, KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_CTRL_RIGHT,
            KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_ALT_RIGHT, KeyEvent.KEYCODE_META_LEFT, KeyEvent.KEYCODE_META_RIGHT,
        )

        val COMMAND_KEYS = mapOf(
            KeyEvent.KEYCODE_ENTER to Vk.RETURN,
            KeyEvent.KEYCODE_NUMPAD_ENTER to Vk.RETURN,
            KeyEvent.KEYCODE_DEL to Vk.BACK,
            KeyEvent.KEYCODE_FORWARD_DEL to Vk.DELETE,
            KeyEvent.KEYCODE_TAB to Vk.TAB,
            KeyEvent.KEYCODE_ESCAPE to Vk.ESCAPE,
            KeyEvent.KEYCODE_SPACE to Vk.SPACE,
            KeyEvent.KEYCODE_DPAD_LEFT to Vk.LEFT,
            KeyEvent.KEYCODE_DPAD_UP to Vk.UP,
            KeyEvent.KEYCODE_DPAD_RIGHT to Vk.RIGHT,
            KeyEvent.KEYCODE_DPAD_DOWN to Vk.DOWN,
            KeyEvent.KEYCODE_MOVE_HOME to Vk.HOME,
            KeyEvent.KEYCODE_MOVE_END to Vk.END,
            KeyEvent.KEYCODE_PAGE_UP to Vk.PAGE_UP,
            KeyEvent.KEYCODE_PAGE_DOWN to Vk.PAGE_DOWN,
            KeyEvent.KEYCODE_INSERT to Vk.INSERT,
            KeyEvent.KEYCODE_SYSRQ to Vk.PRINT_SCREEN,
            KeyEvent.KEYCODE_CAPS_LOCK to Vk.CAPITAL,
            KeyEvent.KEYCODE_SHIFT_LEFT to Vk.SHIFT,
            KeyEvent.KEYCODE_SHIFT_RIGHT to Vk.SHIFT,
            KeyEvent.KEYCODE_CTRL_LEFT to Vk.CONTROL,
            KeyEvent.KEYCODE_CTRL_RIGHT to Vk.CONTROL,
            KeyEvent.KEYCODE_ALT_LEFT to Vk.ALT,
            KeyEvent.KEYCODE_ALT_RIGHT to Vk.ALT,
            KeyEvent.KEYCODE_META_LEFT to Vk.WIN,
            KeyEvent.KEYCODE_META_RIGHT to Vk.WIN,
        )
    }
}
