package ponte.core

/** O que uma tecla vinda de quem acessa vira no celular. */
sealed class PhoneInput {
    /** Digitar este texto no campo em foco. */
    data class Type(val text: String) : PhoneInput()

    /** Um botão ou uma tecla de edição. */
    data class Do(val action: PhoneAction) : PhoneInput()
}

enum class PhoneAction {
    BACK, HOME, RECENTS, NOTIFICATIONS,
    ENTER, BACKSPACE, DELETE, TAB, LEFT, RIGHT, UP, DOWN, LINE_START, LINE_END,
    COPY, CUT, PASTE, SELECT_ALL,
}

/**
 * Traduz as teclas virtuais do Windows (mensagem Key) para o celular. O Ponte do Windows 1.14.0 em diante manda
 * o texto já pronto quando acessa um celular; as teclas de letras daqui servem para um visualizador anterior e
 * seguem o teclado americano (acentos de teclas mortas não vêm por elas).
 */
class PhoneKeyboard {
    private var shift = false
    private var control = false
    private var alt = false

    fun key(virtualKey: Int, up: Boolean): PhoneInput? {
        when (virtualKey) {
            0x10, 0xA0, 0xA1 -> { shift = !up; return null }
            0x11, 0xA2, 0xA3 -> { control = !up; return null }
            0x12, 0xA4, 0xA5 -> { alt = !up; return null }
        }
        if (up) return null
        special(virtualKey)?.let { return PhoneInput.Do(it) }
        if (control) {
            return when (virtualKey) {
                0x41 -> PhoneInput.Do(PhoneAction.SELECT_ALL)
                0x43 -> PhoneInput.Do(PhoneAction.COPY)
                0x56 -> PhoneInput.Do(PhoneAction.PASTE)
                0x58 -> PhoneInput.Do(PhoneAction.CUT)
                else -> null
            }
        }
        if (alt) return null
        return character(virtualKey, shift)?.let { PhoneInput.Type(it.toString()) }
    }

    /** Solta tudo (fim da sessão ou a janela de quem acessa perdeu o foco). */
    fun reset() {
        shift = false
        control = false
        alt = false
    }

    companion object {
        fun special(virtualKey: Int): PhoneAction? = when (virtualKey) {
            0x1B, PhoneKeys.BACK -> PhoneAction.BACK
            0x5B, 0x5C, PhoneKeys.HOME -> PhoneAction.HOME
            PhoneKeys.RECENTS -> PhoneAction.RECENTS
            0x0D -> PhoneAction.ENTER
            0x08 -> PhoneAction.BACKSPACE
            0x2E -> PhoneAction.DELETE
            0x09 -> PhoneAction.TAB
            0x25 -> PhoneAction.LEFT
            0x26 -> PhoneAction.UP
            0x27 -> PhoneAction.RIGHT
            0x28 -> PhoneAction.DOWN
            0x24 -> PhoneAction.LINE_START
            0x23 -> PhoneAction.LINE_END
            0x70 -> PhoneAction.NOTIFICATIONS // F1
            else -> null
        }

        private const val DIGITS_SHIFTED = ")!@#$%^&*("

        fun character(virtualKey: Int, shift: Boolean): Char? = when (virtualKey) {
            in 0x41..0x5A -> (if (shift) 'A' else 'a') + (virtualKey - 0x41)
            in 0x30..0x39 -> if (shift) DIGITS_SHIFTED[virtualKey - 0x30] else '0' + (virtualKey - 0x30)
            in 0x60..0x69 -> '0' + (virtualKey - 0x60)
            0x20 -> ' '
            0x6A -> '*'
            0x6B -> '+'
            0x6D -> '-'
            0x6E -> '.'
            0x6F -> '/'
            0xBA -> if (shift) ':' else ';'
            0xBB -> if (shift) '+' else '='
            0xBC -> if (shift) '<' else ','
            0xBD -> if (shift) '_' else '-'
            0xBE -> if (shift) '>' else '.'
            0xBF -> if (shift) '?' else '/'
            0xC0 -> if (shift) '~' else '`'
            0xDB -> if (shift) '{' else '['
            0xDC -> if (shift) '|' else '\\'
            0xDD -> if (shift) '}' else ']'
            0xDE -> if (shift) '"' else '\''
            else -> null
        }
    }
}
